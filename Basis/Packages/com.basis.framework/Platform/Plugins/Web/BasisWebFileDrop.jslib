var BasisWebFileDropLibrary = {
  $BasisWebFileDrop: {
    ready: [],
    installed: false,
    batch: 0,
    limit: 1024 * 1024 * 1024,

    hasFiles: function (event) {
      var types = event.dataTransfer && event.dataTransfer.types;
      if (!types) return false;
      for (var i = 0; i < types.length; i++) if (types[i] === 'Files') return true;
      return false;
    },

    over: function (event) {
      if (!BasisWebFileDrop.hasFiles(event)) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = 'copy';
    },

    safeName: function (name) {
      name = (name || 'dropped').replace(/[\/\\\0]/g, '_');
      return name.charAt(0) === '.' ? '_' + name : name;
    },

    drop: function (event) {
      if (!BasisWebFileDrop.hasFiles(event)) return;
      event.preventDefault();
      var files = Array.prototype.slice.call(event.dataTransfer.files || []);
      if (!files.length) return;
      var dir = '/tmp/basis-drop/' + (++BasisWebFileDrop.batch);
      var total = 0;
      var reads = files.map(function (file) {
        total += file.size;
        if (total > BasisWebFileDrop.limit) {
          console.warn('[Basis] Dropped file skipped, the batch is over 1 GB: ' + file.name);
          return Promise.resolve(null);
        }
        return file.arrayBuffer().then(function (buffer) {
          var path = dir + '/' + BasisWebFileDrop.safeName(file.name);
          FS.mkdirTree(dir);
          FS.writeFile(path, new Uint8Array(buffer));
          return path;
        }, function (error) {
          console.warn('[Basis] Dropped file could not be read: ' + file.name, error);
          return null;
        });
      });
      Promise.all(reads).then(function (paths) {
        paths = paths.filter(function (p) { return p; });
        if (paths.length) BasisWebFileDrop.ready.push(paths.join('\n'));
      });
    }
  },

  BasisWebFileDrop_Install: function () {
    if (BasisWebFileDrop.installed || typeof document === 'undefined') return;
    BasisWebFileDrop.installed = true;
    document.addEventListener('dragover', BasisWebFileDrop.over, false);
    document.addEventListener('drop', BasisWebFileDrop.drop, false);
  },

  BasisWebFileDrop_Uninstall: function () {
    if (!BasisWebFileDrop.installed) return;
    BasisWebFileDrop.installed = false;
    document.removeEventListener('dragover', BasisWebFileDrop.over, false);
    document.removeEventListener('drop', BasisWebFileDrop.drop, false);
  },

  BasisWebFileDrop_Poll: function () {
    var batch = BasisWebFileDrop.ready.shift();
    if (batch === undefined) return 0;
    var size = lengthBytesUTF8(batch) + 1;
    var buffer = _malloc(size);
    stringToUTF8(batch, buffer, size);
    return buffer;
  }
};

autoAddDeps(BasisWebFileDropLibrary, '$BasisWebFileDrop');
mergeInto(LibraryManager.library, BasisWebFileDropLibrary);
