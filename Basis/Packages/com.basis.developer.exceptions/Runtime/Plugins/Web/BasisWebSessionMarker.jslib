var BasisWebSessionMarkerLibrary = {
  $BasisWebSessionMarker: {
    key: 'basis.session.active',
    lockPrefix: 'basis.session.',
    staleMs: 180000,
    beatMs: 30000,
    id: null,
    timer: 0,

    store: function () {
      try {
        return typeof window !== 'undefined' ? window.localStorage : null;
      } catch (e) {
        return null;
      }
    },

    read: function (store) {
      try {
        var value = JSON.parse(store.getItem(BasisWebSessionMarker.key) || '{}');
        return value && typeof value === 'object' ? value : {};
      } catch (e) {
        return {};
      }
    },

    write: function (store, sessions) {
      try {
        if (Object.keys(sessions).length) store.setItem(BasisWebSessionMarker.key, JSON.stringify(sessions));
        else store.removeItem(BasisWebSessionMarker.key);
      } catch (e) {
      }
    },

    halted: function () {
      return (typeof ABORT !== 'undefined' && !!ABORT) || (typeof ExceptionsSeen !== 'undefined' && !!ExceptionsSeen);
    },

    update: function (change) {
      var store = BasisWebSessionMarker.store();
      if (!store || !BasisWebSessionMarker.id) return;
      var sessions = BasisWebSessionMarker.read(store);
      change(sessions);
      BasisWebSessionMarker.write(store, sessions);
    },

    beat: function () {
      var crashed = BasisWebSessionMarker.halted();
      BasisWebSessionMarker.update(function (sessions) {
        sessions[BasisWebSessionMarker.id] = { t: Date.now(), crashed: crashed };
      });
      if (crashed && BasisWebSessionMarker.timer) {
        clearInterval(BasisWebSessionMarker.timer);
        BasisWebSessionMarker.timer = 0;
      }
    },

    leave: function () {
      if (BasisWebSessionMarker.halted()) {
        BasisWebSessionMarker.beat();
        return;
      }
      BasisWebSessionMarker.update(function (sessions) {
        delete sessions[BasisWebSessionMarker.id];
      });
    }
  },

  BasisWebSessionMarker_Begin: function () {
    var store = BasisWebSessionMarker.store();
    if (!store) return 0;
    var now = Date.now(), crashed = 0, sessions = BasisWebSessionMarker.read(store);
    var held = Module['basisHeldSessionLocks'];
    for (var id in sessions) {
      var session = sessions[id];
      if (session && !session.crashed) {
        if (held ? held.indexOf(BasisWebSessionMarker.lockPrefix + id) >= 0 : now - session.t < BasisWebSessionMarker.staleMs) continue;
      }
      crashed = 1;
      delete sessions[id];
    }
    if (!BasisWebSessionMarker.id) {
      BasisWebSessionMarker.id = now.toString(36) + Math.random().toString(36).slice(2);
      window.addEventListener('pagehide', BasisWebSessionMarker.leave);
      window.addEventListener('pageshow', function (event) {
        if (event.persisted) BasisWebSessionMarker.beat();
      });
      BasisWebSessionMarker.timer = setInterval(BasisWebSessionMarker.beat, BasisWebSessionMarker.beatMs);
      try {
        if (navigator.locks && navigator.locks.request) navigator.locks.request(BasisWebSessionMarker.lockPrefix + BasisWebSessionMarker.id, function () { return new Promise(function () {}); });
      } catch (e) {
      }
    }
    sessions[BasisWebSessionMarker.id] = { t: now, crashed: false };
    BasisWebSessionMarker.write(store, sessions);
    return crashed;
  },

  BasisWebSessionMarker_End: function () {
    if (BasisWebSessionMarker.timer) {
      clearInterval(BasisWebSessionMarker.timer);
      BasisWebSessionMarker.timer = 0;
    }
    BasisWebSessionMarker.update(function (sessions) {
      delete sessions[BasisWebSessionMarker.id];
    });
  }
};

autoAddDeps(BasisWebSessionMarkerLibrary, '$BasisWebSessionMarker');
mergeInto(LibraryManager.library, BasisWebSessionMarkerLibrary);
