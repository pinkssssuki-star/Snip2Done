/*
 * WebView2 版 window.api。
 *
 * 存在的意义：panel.js / detail.js / record.js 一行都不用改。
 * Electron 里 contextBridge 把 preload 的接口挂到 window.api；
 * 这里用 chrome.webview.postMessage 实现同一组方法，签名逐个对齐。
 *
 * 由 C# 通过 AddScriptToExecuteOnDocumentCreatedAsync 注入，保证早于 panel.js 执行。
 */
(function () {
  'use strict';

  var wv = window.chrome && window.chrome.webview;
  if (!wv) {
    console.error('[shim] chrome.webview 不存在，window.api 无法安装');
    return;
  }

  var pending = new Map();
  var stateCbs = [];
  var detailCbs = [];
  var seq = 0;

  function invoke(channel) {
    var args = Array.prototype.slice.call(arguments, 1);
    return new Promise(function (resolve, reject) {
      var id = ++seq;
      pending.set(id, { resolve: resolve, reject: reject });
      wv.postMessage({ kind: 'invoke', id: id, channel: channel, args: args });
    });
  }

  function send(channel) {
    var args = Array.prototype.slice.call(arguments, 1);
    wv.postMessage({ kind: 'send', channel: channel, args: args });
  }

  wv.addEventListener('message', function (e) {
    var m = e.data;
    if (!m || typeof m !== 'object') return;

    if (m.kind === 'result') {
      var p = pending.get(m.id);
      if (!p) return;
      pending.delete(m.id);
      if (m.ok) p.resolve(m.value);
      else p.reject(new Error(m.error || 'invoke 失败'));
    } else if (m.kind === 'state') {
      stateCbs.forEach(function (cb) { cb(m.value); });
    } else if (m.kind === 'detail') {
      detailCbs.forEach(function (cb) { cb(m.value); });
    } else if (m.kind === 'eval') {
      try { (0, eval)(m.code); } catch (err) { console.error('[shim] eval 失败', err); }
    }
  });

  window.api = {
    getState: function () { return invoke('state:get'); },

    touch: function (id) { return invoke('item:touch', id); },
    done: function (payload) { return invoke('item:done', payload); },
    bin: function (id) { return invoke('item:bin', id); },
    restore: function (id) { return invoke('item:restore', id); },
    remove: function (id) { return invoke('item:remove', id); },
    addManual: function (payload) { return invoke('item:addManual', payload); },
    openDetail: function (id) { return invoke('item:openDetail', id); },
    update: function (id, patch) { return invoke('item:update', { id: id, patch: patch }); },

    previewShow: function (id, top, height) { send('preview:show', id, top, height); },
    previewHide: function () { send('preview:hide'); },

    file: function (id) { return invoke('item:file', id); },
    recordOpen: function (id, mode) { return invoke('record:open', { id: id, mode: mode }); },

    seedDemo: function () { return invoke('demo:seed'); },
    setSetting: function (key, value) { return invoke('settings:set', { key: key, value: value }); },

    startCapture: function () { return invoke('capture:start'); },
    quit: function () { return invoke('panel:quit'); },
    hide: function () { return invoke('panel:hide'); },
    resize: function (width, height) { return invoke('panel:resize', { width: width, height: height }); },

    onState: function (cb) { stateCbs.push(cb); },
    onDetail: function (cb) { detailCbs.push(cb); },

    ping: function (tag) { wv.postMessage({ kind: 'ping', tag: tag, t: performance.now() }); },
  };

  /*
   * WebView2 不认 -webkit-app-region: drag（panel.css 里有），
   * 所以改走原生：按住标题栏 → 通知 C# 走 WM_NCLBUTTONDOWN 拖窗。
   */
  function enableDrag() {
    var hdr = document.querySelector('.hdr');
    if (!hdr) return;
    hdr.addEventListener('mousedown', function (ev) {
      if (ev.button !== 0) return;
      if (ev.target.closest('button, input, .acts, a')) return;
      wv.postMessage({ kind: 'drag' });
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', enableDrag);
  else enableDrag();

  console.log('[shim] window.api 就位（WebView2 后端）');
})();
