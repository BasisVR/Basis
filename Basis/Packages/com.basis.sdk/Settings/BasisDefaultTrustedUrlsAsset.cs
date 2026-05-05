using System.Collections.Generic;
using UnityEngine;

namespace Basis.BasisUI
{
    [CreateAssetMenu(fileName = "BasisDefaultTrustedUrls", menuName = "Basis/Default Trusted URLs", order = 1)]
    public class BasisDefaultTrustedUrlsAsset : ScriptableObject
    {
        public List<string> Urls = new List<string>();
    }
}
