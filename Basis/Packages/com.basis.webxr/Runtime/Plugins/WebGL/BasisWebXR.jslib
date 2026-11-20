var BasisWebXRLibrary = {
  BasisWebXR_SetData: function (data) {
    Module.BasisWebXR.data = typeof data === 'bigint' ? Number(data) : data;
  },

  BasisWebXR_Haptic: function (hand, amplitude, duration) {
    Module.BasisWebXR.haptic(hand, amplitude, duration);
  },

  BasisWebXR_Supported: function () {
    return Module.BasisWebXR.supported;
  },

  BasisWebXR_Request: function () {
    Module.BasisWebXR.request();
  },

  BasisWebXR_End: function () {
    Module.BasisWebXR.end();
  },

  BasisWebXR_State: function () {
    return Module.BasisWebXR.state;
  },

  BasisWebXR_Error: function () {
    return Module.BasisWebXR.error;
  }
};

mergeInto(LibraryManager.library, BasisWebXRLibrary);
