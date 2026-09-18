'use strict';

const { contextBridge, ipcRenderer } = require('electron');

/** 悬停预览小窗 —— 只做一件事：收到图片就显示 */
contextBridge.exposeInMainWorld('pv', {
  onInit: (cb) => ipcRenderer.on('preview:init', (_e, payload) => cb(payload)),
});
