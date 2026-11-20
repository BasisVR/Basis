Module['basisHeldSessionLocks'] = null;
(function () {
  try {
    if (typeof navigator === 'undefined' || !navigator.locks || !navigator.locks.query) return;
    navigator.locks.query().then(function (state) {
      Module['basisHeldSessionLocks'] = (state.held || []).map(function (lock) { return lock.name; });
    }, function () {
    });
  } catch (e) {
  }
})();
