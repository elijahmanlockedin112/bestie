// WebPush.cs - notifications to the phone app (Web Push: RFC 8291 encryption, RFC 8292 VAPID). The old .NET
// Framework has no P-256 key agreement that gives the raw secret and no AES-GCM, so both are done here in plain
// managed code (BigInteger curve math, AES blocks + GHASH). Checked against RFC 8291's worked example.
// Nothing here is secret-timing sensitive: it runs on the user's own computer. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Bestie
{
    internal static class WebPush
    {
        // ================================================================== base64url

        public static string B64u(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        public static byte[] FromB64u(string s)
        {
            s = (s ?? "").Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }

        // ================================================================== P-256

        static readonly BigInteger P = Hex("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
        static readonly BigInteger N = Hex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
        static readonly BigInteger CurveB = Hex("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");
        static readonly Pt G = new Pt(Hex("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296"), Hex("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5"));

        sealed class Pt
        {
            public readonly BigInteger X, Y; public readonly bool Inf;
            public Pt(BigInteger x, BigInteger y) { X = x; Y = y; }
            Pt() { Inf = true; }
            public static readonly Pt Infinity = new Pt();
        }

        static BigInteger Hex(string h) { return BigInteger.Parse("0" + h, NumberStyles.HexNumber); }
        static BigInteger Mod(BigInteger x, BigInteger m) { BigInteger r = x % m; return r.Sign < 0 ? r + m : r; }
        static BigInteger Inv(BigInteger x, BigInteger m) { return BigInteger.ModPow(Mod(x, m), m - 2, m); }

        static Pt Add(Pt a, Pt b)
        {
            if (a.Inf) return b;
            if (b.Inf) return a;
            BigInteger l;
            if (a.X == b.X)
            {
                if (Mod(a.Y + b.Y, P).IsZero) return Pt.Infinity;
                l = Mod(3 * a.X * a.X - 3, P) * Inv(2 * a.Y, P);   // a = -3
            }
            else l = Mod(b.Y - a.Y, P) * Inv(b.X - a.X, P);
            l = Mod(l, P);
            BigInteger x = Mod(l * l - a.X - b.X, P);
            return new Pt(x, Mod(l * (a.X - x) - a.Y, P));
        }

        static Pt Mul(BigInteger k, Pt p)
        {
            Pt r = Pt.Infinity, q = p;
            while (k.Sign > 0)
            {
                if (!k.IsEven) r = Add(r, q);
                q = Add(q, q);
                k >>= 1;
            }
            return r;
        }

        static BigInteger FromBE(byte[] b, int off, int len)
        {
            var le = new byte[len + 1];
            for (int i = 0; i < len; i++) le[i] = b[off + len - 1 - i];
            return new BigInteger(le);
        }

        static byte[] ToBE32(BigInteger x)
        {
            byte[] le = x.ToByteArray();
            var be = new byte[32];
            for (int i = 0; i < 32 && i < le.Length; i++) be[31 - i] = le[i];
            return be;
        }

        static Pt Decode(byte[] pub)
        {
            if (pub == null || pub.Length != 65 || pub[0] != 4) throw new ArgumentException("not an uncompressed P-256 point");
            var p = new Pt(FromBE(pub, 1, 32), FromBE(pub, 33, 32));
            if (Mod(p.Y * p.Y - (p.X * p.X * p.X - 3 * p.X + CurveB), P) != 0) throw new ArgumentException("point is not on P-256");
            return p;
        }

        static byte[] Encode(Pt p) { return new byte[] { 4 }.Concat(ToBE32(p.X)).Concat(ToBE32(p.Y)).ToArray(); }

        public static byte[] NewPrivateKey()
        {
            var b = new byte[32];
            using (var rng = new RNGCryptoServiceProvider())
                while (true)
                {
                    rng.GetBytes(b);
                    BigInteger d = FromBE(b, 0, 32);
                    if (d.Sign > 0 && d < N) return b;
                }
        }

        public static byte[] PublicKey(byte[] privateKey) { return Encode(Mul(FromBE(privateKey, 0, 32), G)); }

        /// <summary>The ECDH shared secret: the x coordinate of d·Q, 32 bytes big-endian.</summary>
        public static byte[] SharedSecret(byte[] privateKey, byte[] otherPublic)
        {
            Pt s = Mul(FromBE(privateKey, 0, 32), Decode(otherPublic));
            if (s.Inf) throw new ArgumentException("bad key agreement");
            return ToBE32(s.X);
        }

        /// <summary>ES256 (ECDSA P-256 over SHA-256), as the 64-byte r‖s that JWTs use.</summary>
        public static byte[] SignEs256(byte[] privateKey, byte[] message)
        {
            BigInteger d = FromBE(privateKey, 0, 32), z;
            using (var sha = SHA256.Create()) { byte[] h = sha.ComputeHash(message); z = FromBE(h, 0, 32); }
            while (true)
            {
                BigInteger k = FromBE(NewPrivateKey(), 0, 32);
                BigInteger r = Mod(Mul(k, G).X, N);
                if (r.IsZero) continue;
                BigInteger s = Mod(Inv(k, N) * (z + r * d), N);
                if (s.IsZero) continue;
                return ToBE32(r).Concat(ToBE32(s)).ToArray();
            }
        }

        // ================================================================== HMAC / AES-128-GCM

        static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            using (var h = new HMACSHA256(key))
            {
                byte[] all = parts.SelectMany(p => p).ToArray();
                return h.ComputeHash(all);
            }
        }

        static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }
        static byte[] Take(byte[] b, int n) { var r = new byte[n]; Array.Copy(b, r, n); return r; }

        /// <summary>AES-128-GCM with a 96-bit nonce and no associated data: ciphertext followed by the 16-byte tag.</summary>
        public static byte[] AesGcmEncrypt(byte[] key, byte[] nonce, byte[] plain)
        {
            using (var aes = new AesManaged { Mode = CipherMode.ECB, Padding = PaddingMode.None, KeySize = 128, Key = key })
            using (ICryptoTransform enc = aes.CreateEncryptor())
            {
                Func<byte[], byte[]> block = inBlock => { var o = new byte[16]; enc.TransformBlock(inBlock, 0, 16, o, 0); return o; };
                byte[] h = block(new byte[16]);
                var j0 = new byte[16];
                Array.Copy(nonce, j0, 12); j0[15] = 1;
                var ct = new byte[plain.Length];
                var counter = (byte[])j0.Clone();
                for (int off = 0; off < plain.Length; off += 16)
                {
                    for (int i = 15; i >= 12; i--) if (++counter[i] != 0) break;   // inc32
                    byte[] ks = block(counter);
                    for (int i = 0; i < 16 && off + i < plain.Length; i++) ct[off + i] = (byte)(plain[off + i] ^ ks[i]);
                }
                // GHASH over the ciphertext and the lengths block
                ulong hHi = BE64(h, 0), hLo = BE64(h, 8), yHi = 0, yLo = 0;
                Action<byte[]> absorb = x => { yHi ^= BE64(x, 0); yLo ^= BE64(x, 8); GfMul(ref yHi, ref yLo, hHi, hLo); };
                for (int off = 0; off < ct.Length; off += 16)
                {
                    var x = new byte[16];
                    Array.Copy(ct, off, x, 0, Math.Min(16, ct.Length - off));
                    absorb(x);
                }
                var lens = new byte[16];
                ulong bits = (ulong)ct.Length * 8;
                for (int i = 0; i < 8; i++) lens[15 - i] = (byte)(bits >> (8 * i));
                absorb(lens);
                byte[] ej0 = block(j0);
                var tag = new byte[16];
                for (int i = 0; i < 8; i++) { tag[i] = (byte)(ej0[i] ^ (byte)(yHi >> (56 - 8 * i))); tag[8 + i] = (byte)(ej0[8 + i] ^ (byte)(yLo >> (56 - 8 * i))); }
                return ct.Concat(tag).ToArray();
            }
        }

        static ulong BE64(byte[] b, int off) { ulong v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | b[off + i]; return v; }

        /// <summary>Y = Y·H in GF(2^128), GCM's bit order.</summary>
        static void GfMul(ref ulong yHi, ref ulong yLo, ulong hHi, ulong hLo)
        {
            ulong zHi = 0, zLo = 0, vHi = hHi, vLo = hLo;
            for (int i = 0; i < 128; i++)
            {
                ulong bit = i < 64 ? (yHi >> (63 - i)) & 1 : (yLo >> (127 - i)) & 1;
                if (bit == 1) { zHi ^= vHi; zLo ^= vLo; }
                bool lsb = (vLo & 1) == 1;
                vLo = (vLo >> 1) | (vHi << 63);
                vHi >>= 1;
                if (lsb) vHi ^= 0xE100000000000000UL;
            }
            yHi = zHi; yLo = zLo;
        }

        // ================================================================== RFC 8291 (aes128gcm)

        /// <summary>The encrypted push body for one subscription. asPrivate and salt are random unless given (tests).</summary>
        public static byte[] Encrypt(byte[] payload, byte[] uaPublic, byte[] authSecret, byte[] asPrivate, byte[] salt)
        {
            asPrivate = asPrivate ?? NewPrivateKey();
            if (salt == null) { salt = new byte[16]; using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(salt); }
            byte[] asPublic = PublicKey(asPrivate);
            byte[] ecdh = SharedSecret(asPrivate, uaPublic);
            byte[] prkKey = Hmac(authSecret, ecdh);
            byte[] ikm = Hmac(prkKey, Ascii("WebPush: info"), new byte[] { 0 }, uaPublic, asPublic, new byte[] { 1 });
            byte[] prk = Hmac(salt, ikm);
            byte[] cek = Take(Hmac(prk, Ascii("Content-Encoding: aes128gcm"), new byte[] { 0, 1 }), 16);
            byte[] nonce = Take(Hmac(prk, Ascii("Content-Encoding: nonce"), new byte[] { 0, 1 }), 12);
            byte[] sealedBody = AesGcmEncrypt(cek, nonce, payload.Concat(new byte[] { 2 }).ToArray());   // 2 = last record, no padding
            var header = new List<byte>(salt);
            header.AddRange(new byte[] { 0, 0, 16, 0 });   // record size 4096
            header.Add((byte)asPublic.Length);
            header.AddRange(asPublic);
            return header.Concat(sealedBody).ToArray();
        }

        // ================================================================== VAPID + sending

        /// <summary>"vapid t=…, k=…" for this push service (RFC 8292).</summary>
        public static string Vapid(string endpoint, byte[] privateKey, byte[] publicKey, string subject)
        {
            var u = new Uri(endpoint);
            long exp = (long)(DateTime.UtcNow.AddHours(12) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            string head = B64u(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
            string claims = B64u(Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object> { { "aud", u.Scheme + "://" + u.Host }, { "exp", exp }, { "sub", subject } })));
            string signed = head + "." + claims;
            string jwt = signed + "." + B64u(SignEs256(privateKey, Encoding.ASCII.GetBytes(signed)));
            return "vapid t=" + jwt + ", k=" + B64u(publicKey);
        }

        /// <summary>Sends one notification. Returns the push service's HTTP status (201 = queued; 404/410 = the
        /// subscription is gone), or 0 if it couldn't be reached.</summary>
        public static int Send(string endpoint, byte[] p256dh, byte[] auth, byte[] payload, byte[] vapidPrivate, byte[] vapidPublic, string subject, out string detail)
        {
            detail = null;
            byte[] body = Encrypt(payload, p256dh, auth, null, null);
            var req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "POST";
            req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            req.ContentType = "application/octet-stream";
            req.Headers["Content-Encoding"] = "aes128gcm";
            req.Headers["TTL"] = "86400";
            req.Headers["Urgency"] = "high";
            req.Headers["Authorization"] = Vapid(endpoint, vapidPrivate, vapidPublic, subject);
            req.ContentLength = body.Length;
            try
            {
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) return (int)resp.StatusCode;
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null) { detail = ex.Message; return 0; }
                int code = (int)r.StatusCode;
                using (r) using (var sr = new StreamReader(r.GetResponseStream())) detail = sr.ReadToEnd();
                return code;
            }
        }
    }
}
