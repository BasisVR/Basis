using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests
{
    /// <summary>
    /// The content walk now asks the selector per runtime Type instead of hashing FullName per
    /// component. The memo must give exactly the answer the string set gives — approved, unapproved,
    /// and repeated lookups — because a wrong yes here is a sandbox hole.
    /// </summary>
    public class BasisContentPoliceApprovalMemoTests
    {
        private ContentPoliceSelector authored;
        private ContentPoliceSelector selector;

        [SetUp]
        public void SetUp()
        {
            // CreateInstance fires OnEnable (and so BuildCache) before the types can be added, the
            // way a freshly-authored asset would be empty. Instantiate re-runs OnEnable over the
            // serialized list — the shape a selector loaded from an asset actually has at runtime.
            authored = ScriptableObject.CreateInstance<ContentPoliceSelector>();
            authored.selectedTypes.Add(typeof(BoxCollider).FullName);
            authored.selectedTypes.Add(typeof(AudioSource).FullName);
            selector = Object.Instantiate(authored);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(selector);
            Object.DestroyImmediate(authored);
        }

        [Test]
        public void ApprovedTypesSayYes_AndMatchTheStringSet()
        {
            Assert.That(selector.IsTypeApproved(typeof(BoxCollider)), Is.True);
            Assert.That(selector.IsTypeApproved(typeof(AudioSource)), Is.True);
            Assert.That(selector.IsTypeApproved(typeof(BoxCollider)),
                Is.EqualTo(selector.ApprovedTypeNames.Contains(typeof(BoxCollider).FullName)));
        }

        [Test]
        public void UnapprovedTypesSayNo_EveryTime()
        {
            Assert.That(selector.IsTypeApproved(typeof(Light)), Is.False);
            Assert.That(selector.IsTypeApproved(typeof(Light)), Is.False, "the memoized answer must stay no");
            Assert.That(selector.IsTypeApproved(typeof(Light)),
                Is.EqualTo(selector.ApprovedTypeNames.Contains(typeof(Light).FullName)));
        }

        [Test]
        public void EveryComponentTypeAgreesWithTheStringProbe()
        {
            foreach (var type in new[] { typeof(BoxCollider), typeof(AudioSource), typeof(Light), typeof(UnityEngine.Camera), typeof(MeshRenderer), typeof(Rigidbody) })
            {
                bool memo = selector.IsTypeApproved(type);
                bool direct = selector.ApprovedTypeNames.Contains(type.FullName);
                Assert.That(memo, Is.EqualTo(direct), type.FullName);
            }
        }
    }
}
