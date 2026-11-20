Module['autoSyncPersistentDataPath'] = true;
(function () {
  if (typeof window === 'undefined') return;
  var flush = function () {
    try {
      if (typeof IDBFS !== 'undefined' && Module.__unityIdbfsMount) IDBFS.queuePersist(Module.__unityIdbfsMount.mount);
    } catch (e) {
    }
  };
  window.addEventListener('pagehide', flush);
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'hidden') flush();
  });
  var storage = navigator.storage;
  if (!storage || !storage.persist || !storage.persisted) return;
  var asked = false;
  var request = function () {
    if (asked) return;
    asked = true;
    window.removeEventListener('pointerdown', request, true);
    window.removeEventListener('keydown', request, true);
    storage.persisted().then(function (persisted) {
      return persisted || storage.persist();
    }).then(function (persisted) {
      if (!persisted) console.warn('[Basis] The browser did not grant persistent storage, saved data may be cleared when space runs low.');
    }, function () {
    });
  };
  window.addEventListener('pointerdown', request, true);
  window.addEventListener('keydown', request, true);
})();
