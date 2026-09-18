'use strict';

const img = document.getElementById('img');

window.pv.onInit(({ src }) => {
  if (src) img.src = src;
});
