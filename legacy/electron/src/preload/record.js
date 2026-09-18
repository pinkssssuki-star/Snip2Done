'use strict';

const { contextBridge, ipcRenderer } = require('electron');

/** 完成记录窗 */
contextBridge.exposeInMainWorld('rec', {
  onInit: (cb) => ipcRenderer.on('record:init', (_e, payload) => cb(payload)),
  commit: (id, reflection) => ipcRenderer.invoke('record:commit', { id, reflection }),
  close: () => ipcRenderer.send('record:close'),
});
