'use strict';

const { contextBridge, ipcRenderer } = require('electron');

/** 框选 overlay 与「去向选择器」共用这个 preload */
contextBridge.exposeInMainWorld('ov', {
  // --- 框选 overlay ---
  ready: () => ipcRenderer.send('overlay:ready'),
  onInit: (cb) => ipcRenderer.on('overlay:init', (_e, payload) => cb(payload)),
  /** 截图已经解码完、可以显示了（主进程据此才把窗口显示出来） */
  painted: () => ipcRenderer.send('overlay:painted'),
  /** 诊断用：把渲染层的量测值打到主进程日志 */
  debug: (msg) => ipcRenderer.send('overlay:debug', msg),
  pick: (rect) => ipcRenderer.send('overlay:pick', rect),
  cancel: () => ipcRenderer.send('overlay:cancel'),

  // --- 去向选择器 ---
  onChooserInit: (cb) => ipcRenderer.on('chooser:init', (_e, payload) => cb(payload)),
  /** title 可选：用户手填的标题，留空就走「前台窗口标题」 */
  choose: (choice, title) => ipcRenderer.send('chooser:pick', choice, title),
  cancelChooser: () => ipcRenderer.send('chooser:cancel'),
});
