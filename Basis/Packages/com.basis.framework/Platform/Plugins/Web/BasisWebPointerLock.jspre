(function () {
  if (typeof Element === 'undefined' || !Element.prototype.requestPointerLock) return;
  var request = Element.prototype.requestPointerLock;
  Element.prototype.requestPointerLock = function () {
    var result = request.apply(this, arguments);
    if (result && typeof result.catch === 'function') {
      result.catch(function (error) {
        console.warn('[Basis] Pointer lock was refused: ' + error);
      });
    }
    return result;
  };
})();
