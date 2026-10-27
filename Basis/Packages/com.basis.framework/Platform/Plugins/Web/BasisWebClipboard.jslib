var BasisWebClipboardLibrary = {
  $BasisWebClipboard: {
    pending: null,
    listening: false,

    ptr: function (value) {
      return typeof value === 'bigint' ? Number(value) : value;
    },

    legacy: function (text) {
      if (typeof document === 'undefined' || !document.body) return false;
      var area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      area.style.position = 'fixed';
      area.style.top = '-1000px';
      area.style.opacity = '0';
      var focused = document.activeElement;
      document.body.appendChild(area);
      area.select();
      var copied = false;
      try { copied = document.execCommand('copy'); } catch (e) {}
      document.body.removeChild(area);
      if (focused && focused.focus) focused.focus();
      return copied;
    },

    flush: function () {
      var text = BasisWebClipboard.pending;
      if (text === null) return;
      BasisWebClipboard.pending = null;
      if (!BasisWebClipboard.legacy(text)) console.warn('[Basis] Clipboard write was refused by the browser.');
    },

    defer: function (text) {
      BasisWebClipboard.pending = text;
      if (BasisWebClipboard.listening || typeof document === 'undefined') return;
      BasisWebClipboard.listening = true;
      document.addEventListener('pointerup', BasisWebClipboard.flush, true);
      document.addEventListener('keyup', BasisWebClipboard.flush, true);
    },

    write: function (text) {
      if (typeof navigator !== 'undefined' && navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(function () {
          if (BasisWebClipboard.pending === text) BasisWebClipboard.pending = null;
        }, function () {
          if (!BasisWebClipboard.legacy(text)) BasisWebClipboard.defer(text);
        });
        return;
      }
      if (!BasisWebClipboard.legacy(text)) BasisWebClipboard.defer(text);
    }
  },

  BasisWebClipboard_Copy: function (textPtr) {
    BasisWebClipboard.write(UTF8ToString(BasisWebClipboard.ptr(textPtr)));
  }
};

autoAddDeps(BasisWebClipboardLibrary, '$BasisWebClipboard');
mergeInto(LibraryManager.library, BasisWebClipboardLibrary);
