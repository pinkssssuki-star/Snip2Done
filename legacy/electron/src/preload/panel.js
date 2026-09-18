'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('api', {
  getState: () => ipcRenderer.invoke('state:get'),

  touch: (id) => ipcRenderer.invoke('item:touch', id),
  // payload 可以是 id 字符串，也可以是 { id, reflection, file }
  done: (payload) => ipcRenderer.invoke('item:done', payload),
  bin: (id) => ipcRenderer.invoke('item:bin', id),
  restore: (id) => ipcRenderer.invoke('item:restore', id),
  remove: (id) => ipcRenderer.invoke('item:remove', id),
  addManual: (payload) => ipcRenderer.invoke('item:addManual', payload),
  openDetail: (id) => ipcRenderer.invoke('item:openDetail', id),
  update: (id, patch) => ipcRenderer.invoke('item:update', { id, patch }),

  // 悬停预览（长悬停时在面板左侧弹出放大图）
  previewShow: (id, top, height) => ipcRenderer.send('preview:show', { id, top, height }),
  previewHide: () => ipcRenderer.send('preview:hide'),

  // 完成链路
  file: (id) => ipcRenderer.invoke('item:file', id),
  recordOpen: (id, mode) => ipcRenderer.invoke('record:open', { id, mode }),

  /** 首次使用：灌入演示数据（仅在库为空时生效） */
  seedDemo: () => ipcRenderer.invoke('demo:seed'),

  setSetting: (key, value) => ipcRenderer.invoke('settings:set', { key, value }),

  startCapture: () => ipcRenderer.invoke('capture:start'),
  quit: () => ipcRenderer.invoke('panel:quit'),
  hide: () => ipcRenderer.invoke('panel:hide'),
  resize: (width, height) => ipcRenderer.invoke('panel:resize', { width, height }),

  onState: (cb) => {
    ipcRenderer.on('state:changed', (_e, snapshot) => cb(snapshot));
  },
  onDetail: (cb) => {
    ipcRenderer.on('detail:init', (_e, payload) => cb(payload));
  },

  ping: (tag) => ipcRenderer.send('ui:ping', tag),
});
