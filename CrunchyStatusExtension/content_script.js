// Order: 1) iframe.video-player -> Watching, 2) title starts with "Watch" -> Looking, 3) else Browsing
// Sends JSON to ws://127.0.0.1:54231/ with a timestamp for elapsed tracking.

(function () {
  "use strict";

  const WS_URL = "ws://127.0.0.1:54231/";
  const PLAYER_SRC_FRAGMENT = "/vilos-v2/web/vilos/player.html";

  let ws = null;
  let wsOpen = false;
  let reconnectTimer = null;
  let lastSentJson = null;
  let lastState = "";
  let lastUrl = "";
  let stateStartTimestamp = Date.now();

  // ===== WebSocket connection =====
  function connectWebSocket() {
    if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;

    try {
      ws = new WebSocket(WS_URL);
    } catch (e) {
      scheduleReconnect();
      return;
    }

    ws.addEventListener("open", function () {
      wsOpen = true;
      if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
      sendCurrentState();
    });

    ws.addEventListener("close", function () { wsOpen = false; scheduleReconnect(); });
    ws.addEventListener("error", function () {
      wsOpen = false;
      try { if (ws) ws.close(); } catch (e) { }
      scheduleReconnect();
    });
  }

  function scheduleReconnect(delay) {
    if (typeof delay === "undefined" || delay === null) delay = 2000;
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(function () {
      reconnectTimer = null;
      connectWebSocket();
    }, delay);
  }

  function safeSend(obj) {
    try {
      const j = JSON.stringify(obj);
      if (j === lastSentJson) return;
      lastSentJson = j;
      if (!wsOpen) { connectWebSocket(); return; }
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(j);
    } catch (e) { /* ignore */ }
  }

  // ===== DOM helpers =====
  function isCrunchyrollUrl() {
    try { return /(^|\.)crunchyroll\.com$/i.test(new URL(window.location.href).hostname); } catch (e) { return false; }
  }

  function extractTitlesFromDom() {
    let anime = null, episode = null;
    try {
      const h4 = document.querySelector("h4");
      const h1 = document.querySelector("h1");
      if (h4 && h4.innerText) anime = h4.innerText.trim();
      if (h1 && h1.innerText) episode = h1.innerText.trim();
    } catch (e) { }
    return { anime, episode };
  }

  // ===== iframe detection =====
  function findVilosIframe() {
    try {
      const iframe = document.querySelector("iframe.video-player, iframe[title='Video Player']");
      if (!iframe) return null;
      const src = iframe.getAttribute("src") || iframe.src || "";
      if (src.indexOf(PLAYER_SRC_FRAGMENT) >= 0) return iframe;
      if (iframe.classList && iframe.classList.contains("video-player")) return iframe;
      return null;
    } catch (e) { return null; }
  }

  // ===== Display state logic =====
  function determineDisplayState() {
    const title = (document.title || "").trim();
    const iframe = findVilosIframe();

    if (iframe) return { state: "Watching", titleHint: title, reason: "iframe" };
    if (/^Watch\s+/i.test(title)) return { state: "Looking", titleHint: title, reason: "title-watch" };
    return { state: "Browsing", titleHint: "", reason: "default" };
  }

  // ===== Metadata =====
  function extractMetadataForPayload() {
    const dom = extractTitlesFromDom();
    return { source: "dom", title: dom.anime || null, episode: dom.episode || null };
  }

  // ===== Payload builder =====
  async function buildPayload() {
    if (!isCrunchyrollUrl()) return null;

    const disp = determineDisplayState();

    // detect state/url change and refresh timestamp
    if (disp.state !== lastState || window.location.href !== lastUrl) {
      lastState = disp.state;
      lastUrl = window.location.href;
      stateStartTimestamp = Date.now();
    }

    let displayTitle = "";
    if (disp.state === "Looking") {
      displayTitle = (disp.titleHint || "")
        .replace(/^Watch\s+/i, "")
        .replace(/\s+-\s+Crunchyroll$/i, "")
        .trim();
    }

    let meta = null;
    if (disp.state === "Watching") {
      meta = extractMetadataForPayload();
    }

    const payload = {
      displayState: disp.state,
      displayTitle: displayTitle,
      metadata: meta
        ? { source: meta.source, title: meta.title || null, episode: meta.episode || null }
        : { source: "none", title: null, episode: null },
      url: window.location.href,
      timestamp: stateStartTimestamp,
      _debug: { reason: disp.reason }
    };

    return payload;
  }

  async function sendCurrentState() {
    try {
      const payload = await buildPayload();
      if (!payload) return;
      safeSend(payload);
    } catch (e) { }
  }

  // ===== Observers =====
  setInterval(sendCurrentState, 2500);
  window.addEventListener("load", function () { setTimeout(sendCurrentState, 800); });
  setTimeout(sendCurrentState, 600);
  connectWebSocket();

  window.__crunchyBridge = {
    determineDisplayState: determineDisplayState
  };
})();
