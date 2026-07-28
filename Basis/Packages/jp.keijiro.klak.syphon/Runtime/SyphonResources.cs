// We hide the create menu item because we don't expect a user to create an
// instance manually -- They should use the NdiResources.asset file provided
// within this package. To enable the menu item, uncomment the line bellow.
// #define SHOW_MENU_ITEM

using UnityEngine;

namespace Klak.Syphon {

#if SHOW_MENU_ITEM
[CreateAssetMenu(fileName = "SyphonResources",
                 menuName = "ScriptableObjects/KlakSyphon/Syphon Resources")]
#endif
public sealed class SyphonResources : ScriptableObject
{
    public Shader blitShader;
}

} // namespace Klak.Syphon
