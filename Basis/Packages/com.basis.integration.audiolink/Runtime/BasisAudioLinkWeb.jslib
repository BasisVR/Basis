var BasisAudioLinkLibrary = {
  $BasisAudioLink__deps: ['$WEBAudio'],
  $BasisAudioLink: {
    links: {},

    ptr: function (value) {
      return typeof value === 'bigint' ? Number(value) : value;
    },

    fftSize: function (size) {
      var value = 32;
      while (value < size && value < 32768) value *= 2;
      return value;
    },

    clipSeconds: function (clip) {
      if (!clip) return 0;
      if (clip.buffer) return clip.buffer.duration;
      if (clip.mediaElement && isFinite(clip.mediaElement.duration)) return clip.mediaElement.duration;
      return 0;
    },

    stopped: function (channel) {
      return !channel.soundClip || (typeof channel.isStopped === 'function' && channel.isStopped());
    },

    findChannel: function (seconds) {
      if (!WEBAudio.audioContext) return null;
      var instances = WEBAudio.audioInstances;
      for (var key in instances) {
        var channel = instances[key];
        if (!channel || !channel.gain || BasisAudioLink.stopped(channel)) continue;
        if (Math.abs(BasisAudioLink.clipSeconds(channel.soundClip) - seconds) < 0.05) return channel;
      }
      return null;
    },

    unhook: function (link) {
      if (link.channel && link.input) {
        try {
          link.channel.gain.disconnect(link.input);
        } catch (e) {
        }
      }
      link.channel = null;
    },

    attach: function (link) {
      if (link.channel && BasisAudioLink.stopped(link.channel)) BasisAudioLink.unhook(link);
      if (!link.channel) link.channel = BasisAudioLink.findChannel(link.seconds);
      if (!link.channel) return false;
      if (!link.input) {
        var context = WEBAudio.audioContext;
        link.input = context.createGain();
        link.input.channelCount = 2;
        link.input.channelCountMode = 'explicit';
        link.input.channelInterpretation = 'speakers';
        link.splitter = context.createChannelSplitter(2);
        link.left = context.createAnalyser();
        link.right = context.createAnalyser();
        link.left.fftSize = link.size;
        link.right.fftSize = link.size;
        link.input.connect(link.splitter);
        link.splitter.connect(link.left, 0);
        link.splitter.connect(link.right, 1);
      }
      link.channel.gain.connect(link.input);
      return true;
    },

    fetch: function (id, buffer, size, side) {
      var start = BasisAudioLink.ptr(buffer) / 4;
      var target = HEAPF32.subarray(start, start + size);
      var link = BasisAudioLink.links[id];
      try {
        if (link && BasisAudioLink.attach(link)) {
          link[side].getFloatTimeDomainData(target);
          return 1;
        }
      } catch (e) {
      }
      target.fill(0);
      return 0;
    }
  },

  SetupAnalyzerSpace: function () {
    return 1;
  },

  LinkAnalyzer: function (id, duration, bufferSize) {
    var link = BasisAudioLink.links[id];
    if (link) {
      BasisAudioLink.unhook(link);
    } else {
      link = { size: BasisAudioLink.fftSize(bufferSize), channel: null };
      BasisAudioLink.links[id] = link;
    }
    link.seconds = duration;
    try {
      return BasisAudioLink.attach(link) ? 1 : 0;
    } catch (e) {
      return 0;
    }
  },

  UnlinkAnalyzer: function (id) {
    var link = BasisAudioLink.links[id];
    if (!link) return 0;
    BasisAudioLink.unhook(link);
    if (link.input) {
      link.input.disconnect();
      link.splitter.disconnect();
    }
    delete BasisAudioLink.links[id];
    return 1;
  },

  FetchAnalyzerLeft: function (id, buffer, size) {
    return BasisAudioLink.fetch(id, buffer, size, 'left');
  },

  FetchAnalyzerRight: function (id, buffer, size) {
    return BasisAudioLink.fetch(id, buffer, size, 'right');
  }
};

autoAddDeps(BasisAudioLinkLibrary, '$BasisAudioLink');
mergeInto(LibraryManager.library, BasisAudioLinkLibrary);
