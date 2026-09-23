// Tiny status window: is bestie running, are we connected, how many tabs are we sharing.
const APP = "http://127.0.0.1:47823";

function show(status, detail) {
  document.getElementById("status").textContent = status;
  document.getElementById("detail").textContent = detail || "";
}

async function appAwake() {
  try {
    const res = await fetch(APP + "/ping", { cache: "no-store" });
    const data = await res.json();
    return data && data.app === "bestie";
  } catch (e) {
    return false;
  }
}

async function sharedTabs() {
  const tabs = await chrome.tabs.query({});
  const n = tabs.filter((t) => !t.incognito).length;
  return "sharing " + n + (n === 1 ? " tab." : " tabs.");
}

async function refresh(triesLeft) {
  const [awake, stored, tabs] = await Promise.all([
    appAwake(),
    chrome.storage.session.get("status").catch(() => ({})),
    sharedTabs(),
  ]);
  const status = stored.status || {};
  if (!awake) return show("bestie isn't running.", "wake it up and i'll find it.");
  if (status.problem === "paired-elsewhere")
    return show("bestie is paired with another extension.", "ask bestie to forget its browser pairing.");
  if (status.connected) return show("connected to bestie ✓", tabs);
  if (triesLeft > 0) {
    show("found bestie. saying hi…", "");
    setTimeout(() => refresh(triesLeft - 1), 800);
  } else {
    show("bestie is awake but not answering.", "give it a sec, then open this again.");
  }
}

chrome.runtime.sendMessage({ type: "wake" }).catch(() => {});
refresh(6);
