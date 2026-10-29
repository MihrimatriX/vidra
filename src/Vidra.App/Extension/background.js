// Vidra eklentisi: bağlantıları Vidra'ya gönderir ve YouTube oturumunu eşitler.
// Vidra ile native messaging üzerinden konuşur (host: com.vidra.app; Vidra her açılışta kendini kaydeder).
const HOST = 'com.vidra.app';

// Yalnızca oturumu taşıyan çerezler değişince eşitle; SIDCC gibi her istekte değişenler gürültü yapar
const SESSION = new Set([
  'SID', 'HSID', 'SSID', 'APISID', 'SAPISID', 'LOGIN_INFO',
  '__Secure-1PSID', '__Secure-3PSID', '__Secure-1PAPISID', '__Secure-3PAPISID',
  '__Secure-1PSIDTS', '__Secure-3PSIDTS',
]);
const RELEVANT = /(^|\.)(youtube|google)\.com$/;
const YOUTUBE = /^https?:\/\/([a-z]+\.)?(youtube\.com|youtu\.be)\//;

async function send(msg) {
  try {
    return await chrome.runtime.sendNativeMessage(HOST, msg);
  } catch (e) {
    // Vidra kurulu değil ya da host kaydı yok (Vidra'yı bir kez açmak kaydeder)
    return { ok: false, error: String(e) };
  }
}

function badge(text, color) {
  chrome.action.setBadgeText({ text });
  if (color) chrome.action.setBadgeBackgroundColor({ color });
}

async function syncCookies() {
  const [yt, google] = await Promise.all([
    chrome.cookies.getAll({ domain: 'youtube.com' }),
    chrome.cookies.getAll({ domain: 'google.com' }),
  ]);
  const r = await send({ type: 'cookies', cookies: [...yt, ...google] });
  if (!r.ok) badge('×', '#c0392b');
  else badge(r.loggedIn ? '' : '!', '#d68910');
}

let timer;
chrome.cookies.onChanged.addListener(({ cookie }) => {
  if (!SESSION.has(cookie.name) || !RELEVANT.test(cookie.domain)) return;
  // Döndürme birkaç çerezi art arda değiştirir: hepsi yerine oturduktan sonra tek seferde gönder
  clearTimeout(timer);
  timer = setTimeout(syncCookies, 3000);
});

async function open(url) {
  if (!url || !YOUTUBE.test(url)) { badge('?', '#7f8c8d'); return; }
  const r = await send({ type: 'open', url });
  badge(r.ok ? '' : '×', '#c0392b');
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: 'vidra-link', title: "Vidra ile indir", contexts: ['link', 'video'],
    targetUrlPatterns: ['*://*.youtube.com/*', '*://youtu.be/*'],
  });
  chrome.contextMenus.create({
    id: 'vidra-page', title: "Bu sayfayı Vidra'da aç", contexts: ['page'],
    documentUrlPatterns: ['*://*.youtube.com/*'],
  });
  syncCookies();
});
chrome.runtime.onStartup.addListener(syncCookies);

chrome.contextMenus.onClicked.addListener((info, tab) => open(info.linkUrl || info.srcUrl || info.pageUrl || tab?.url));
chrome.action.onClicked.addListener(tab => open(tab.url));
