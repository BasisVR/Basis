using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisMenuShimHardeningTests
    {
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        const char Joiner = (char)0x2060;
        static readonly Assembly Shims = typeof(CilboxBasisCommon).Assembly;
        static readonly Type Menus = Shims.GetType("Cilbox.CilboxMenus", true);
        static readonly Type View = Shims.GetType("Cilbox.CilboxMenuView", true);
        static readonly Type Profiler = Shims.GetType("Cilbox.CilboxProfiler", true);
        static readonly Type Consent = Shims.GetType("Cilbox.CilboxBasisConsent", true);
        static readonly PropertyInfo LocalPlayer = typeof(BasisLocalPlayer).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        readonly List<GameObject> hosts = new List<GameObject>();
        BasisLocalPlayer savedPlayer;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(LocalPlayer, "BasisLocalPlayer.Instance moved; this test cannot stand in for the local player.");
            savedPlayer = BasisLocalPlayer.Instance;
            LocalPlayer.SetValue(null, null);
            BasisUserActivation.Clear();
            BasisAddressablePrefabCache.ResetForTests(path => UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path));
        }

        [TearDown]
        public void TearDown()
        {
            Call(Menus, "Reset");
            SettingsOpen(null);
            BasisUserActivation.Clear();
            LocalPlayer.SetValue(null, savedPlayer);
            BasisAddressablePrefabCache.ResetForTests(null);
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        static object Call(Type type, string name, params object[] args)
        {
            foreach (MethodInfo method in type.GetMethods(Hidden))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name != name || parameters.Length != args.Length) continue;
                bool fits = true;
                for (int i = 0; i < args.Length; i++) fits &= args[i] == null || parameters[i].ParameterType.IsInstanceOfType(args[i]);
                if (!fits) continue;
                try
                {
                    return method.Invoke(null, args);
                }
                catch (TargetInvocationException e)
                {
                    throw e.InnerException;
                }
            }
            Assert.Fail($"{type.Name}.{name} moved; this test cannot reach it.");
            return null;
        }

        static object Member(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, Fields);
            Assert.IsNotNull(field, $"{target.GetType().Name}.{name} moved; this test cannot read it.");
            return field.GetValue(target);
        }

        static void SetMember(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, Fields);
            Assert.IsNotNull(field, $"{target.GetType().Name}.{name} moved; this test cannot set it.");
            field.SetValue(target, value);
        }

        static object State(BasisMenuPage page) => Call(Menus, "Find", page);

        static object Constant(string name)
        {
            FieldInfo field = Menus.GetField(name, Hidden);
            Assert.IsNotNull(field, $"CilboxMenus.{name} moved; this test cannot read the limit.");
            return field.GetValue(null);
        }

        static FieldInfo ProviderField(string name)
        {
            FieldInfo field = typeof(SettingsProvider).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, $"SettingsProvider.{name} moved; this test cannot open Settings.");
            return field;
        }

        static void SettingsOpen(PanelTabGroup group)
        {
            ProviderField("_searchTabGroup").SetValue(null, group);
            ((IDictionary)ProviderField("_tabKeyToIndex").GetValue(null)).Clear();
            ((IList)ProviderField("_lazyTabs").GetValue(null)).Clear();
            ((IList)ProviderField("_tabSearches").GetValue(null)).Clear();
        }

        GameObject Host(string name)
        {
            GameObject host = new GameObject(name);
            hosts.Add(host);
            return host;
        }

        T Box<T>() where T : CilboxBasisCommon
        {
            T box = Host(typeof(T).Name + " menu hardening test").AddComponent<T>();
            box.classes = new Dictionary<string, int>();
            return box;
        }

        static CilboxProxy Proxy(CilboxBasisCommon box)
        {
            CilboxProxy proxy = new GameObject("Script").AddComponent<CilboxProxy>();
            proxy.transform.SetParent(box.transform);
            proxy.box = box;
            return proxy;
        }

        static T Running<T>(CilboxBasisCommon box, Func<T> call)
        {
            box.interpreterAccountingDepth++;
            try
            {
                return call();
            }
            finally
            {
                box.interpreterAccountingDepth--;
            }
        }

        static void Running(CilboxBasisCommon box, Action call) => Running(box, () => { call(); return 0; });

        static BasisProp Local(BasisProp prop)
        {
            prop.AssignContentIdentifier(new BasisNetworkContentBase.BasisContentInformation { LoadedNetID = "menu-hardening", SpawnedByLocalPlayer = true });
            return prop;
        }

        static BasisLocalPlayer Wear(BasisAvatar avatar)
        {
            BasisLocalPlayer player = (BasisLocalPlayer)FormatterServices.GetUninitializedObject(typeof(BasisLocalPlayer));
            player.BasisAvatar = avatar;
            LocalPlayer.SetValue(null, player);
            return player;
        }

        RectTransform Canvas()
        {
            GameObject canvas = new GameObject("Menu hardening canvas", typeof(Canvas));
            hosts.Add(canvas);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(1920, 1080);
            RectTransform host = (RectTransform)new GameObject("Host", typeof(RectTransform)).transform;
            host.SetParent(canvas.transform, false);
            host.sizeDelta = new Vector2(1200, 1000);
            return host;
        }

        static bool Shows(Component root, string description)
        {
            foreach (PanelElementDescriptor element in root.GetComponentsInChildren<PanelElementDescriptor>(true))
            {
                if (element.Description == description) return true;
            }
            return false;
        }

        static void NoEscapes(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\') continue;
                Assert.Less(i + 1, text.Length, "a bare backslash ends the text: " + text);
                Assert.AreEqual(Joiner, text[i + 1], "TextMeshPro would decode what follows this backslash: " + text);
            }
        }

        static string Shown(object label, string text)
        {
            object info = label.GetType().GetMethod("GetTextInfo", new[] { typeof(string) }).Invoke(label, new object[] { text });
            int count = (int)info.GetType().GetField("characterCount").GetValue(info);
            Array characters = (Array)info.GetType().GetField("characterInfo").GetValue(info);
            StringBuilder shown = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                object character = characters.GetValue(i);
                shown.Append((char)character.GetType().GetField("character").GetValue(character));
            }
            return shown.ToString();
        }

        static float Width(object label, string text)
        {
            label.GetType().GetMethod("SetText", new[] { typeof(string) }).Invoke(label, new object[] { text });
            return (float)label.GetType().GetProperty("preferredWidth").GetValue(label);
        }

        [Test]
        public void APropParentedUnderTheWorld_KeepsTheOwnerItHadWhenItLoaded()
        {
            Assume.That(UnityEngine.Object.FindObjectsByType<BasisScene>(FindObjectsInactive.Include), Is.Empty, "the open scene already has a BasisScene, which this test would find instead");
            GameObject world = Host("World");
            world.AddComponent<BasisScene>();

            CilboxPropBasis theirs = Box<CilboxPropBasis>();
            theirs.gameObject.AddComponent<BasisProp>();
            CilboxProxy theirScript = Proxy(theirs);
            Call(Consent, "GateContent", theirs.gameObject);
            theirScript.transform.SetParent(world.transform);
            Assert.IsTrue(BasisContent.TryResolve(theirScript.gameObject, out BasisContentBase under) && under is BasisScene, "the script now sits under the world");
            Assert.IsNull(Running(theirs, () => BasisMenuShim.AddSettingsTab(theirScript, "Session")), "another player's prop must not pass as the world by moving under it");
            Assert.IsNull(Running(theirs, () => BasisMenuShim.AddPanel(theirScript, "Session")));

            CilboxPropBasis mine = Box<CilboxPropBasis>();
            BasisProp prop = Local(mine.gameObject.AddComponent<BasisProp>());
            CilboxProxy myScript = Proxy(mine);
            Call(Consent, "GateContent", mine.gameObject);
            myScript.transform.SetParent(world.transform);
            BasisMenuPage page = Running(mine, () => BasisMenuShim.AddSettingsTab(myScript, "Mine"));
            Assert.IsNotNull(page);
            object state = State(page);
            Assert.AreSame(prop, Member(state, "Content"), "labels, limits and Open() follow the owner recorded at load, not the current parent");
            Assert.IsFalse((bool)Member(state, "World"), "it does not count against the world's limits either");
        }

        [Test]
        public void ADeadScriptsTitlesAndSlots_AreFreeBeforeTheNextTick()
        {
            CilboxSceneBasis old = Box<CilboxSceneBasis>();
            CilboxProxy oldScript = Proxy(old);
            Assert.IsNotNull(Running(old, () => BasisMenuShim.AddSettingsTab(oldScript, "Race Track")));
            Assert.IsNotNull(Running(old, () => BasisMenuShim.AddPanel(oldScript, "Arcade")));
            UnityEngine.Object.DestroyImmediate(oldScript.gameObject);

            CilboxSceneBasis fresh = Box<CilboxSceneBasis>();
            CilboxProxy freshScript = Proxy(fresh);
            Assert.IsNotNull(Running(fresh, () => BasisMenuShim.AddSettingsTab(freshScript, "Race Track")), "a page whose script is gone must not keep its title until the next tick");
            Assert.IsNotNull(Running(fresh, () => BasisMenuShim.AddPanel(freshScript, "Arcade")), "nor its panel");
            Assert.AreEqual(2, (int)Call(Menus, "get_PageCount"));
        }

        [Test]
        public void ASwappedAvatar_GetsItsMenusBackInTheSameFrame()
        {
            CilboxAvatarBasis oldBox = Box<CilboxAvatarBasis>();
            BasisAvatar oldAvatar = oldBox.gameObject.AddComponent<BasisAvatar>();
            oldAvatar.IsOwnedLocally = true;
            CilboxProxy oldScript = Proxy(oldBox);
            BasisLocalPlayer player = Wear(oldAvatar);
            BasisMenuPage oldTab = Running(oldBox, () => BasisMenuShim.AddSettingsTab(oldScript, "My Avatar"));
            Assert.IsNotNull(oldTab);
            Assert.IsNotNull(Running(oldBox, () => BasisMenuShim.AddPanel(oldScript, "Avatar Menu")));

            CilboxAvatarBasis newBox = Box<CilboxAvatarBasis>();
            BasisAvatar newAvatar = newBox.gameObject.AddComponent<BasisAvatar>();
            newAvatar.IsOwnedLocally = true;
            CilboxProxy newScript = Proxy(newBox);
            player.BasisAvatar = newAvatar;
            Assert.IsNotNull(Running(newBox, () => BasisMenuShim.AddSettingsTab(newScript, "My Avatar")), "the old avatar is only destroyed at the end of the frame, but it no longer holds the title");
            Assert.IsNotNull(Running(newBox, () => BasisMenuShim.AddPanel(newScript, "Avatar Menu")));
            Assert.IsFalse(oldTab.IsValid, "the avatar the player took off loses its pages");
            Assert.IsNull(Running(oldBox, () => BasisMenuShim.AddSettingsTab(oldScript, "Still Here")), "and cannot add new ones");
        }

        [Test]
        public void LimitsCountPerContent_SoAWorldCannotCrowdOutThePlayersOwnContent()
        {
            CilboxSceneBasis first = Box<CilboxSceneBasis>(), second = Box<CilboxSceneBasis>();
            CilboxProxy one = Proxy(first), two = Proxy(second);
            for (int i = 0; i < BasisMenuShim.MaxSettingsTabs / 2; i++)
            {
                Assert.IsNotNull(Running(first, () => BasisMenuShim.AddSettingsTab(one, "First " + i)));
                Assert.IsNotNull(Running(second, () => BasisMenuShim.AddSettingsTab(two, "Second " + i)));
            }
            Assert.IsNull(Running(second, () => BasisMenuShim.AddSettingsTab(two, "One More")), "every box of a world shares that world's tabs");
            Assert.IsNotNull(Running(first, () => BasisMenuShim.AddPanel(one, "World Games")));
            Assert.IsNull(Running(second, () => BasisMenuShim.AddPanel(two, "World Shop")), "one panel per content, however many boxes it has");

            CilboxAvatarBasis avatarBox = Box<CilboxAvatarBasis>();
            BasisAvatar avatar = avatarBox.gameObject.AddComponent<BasisAvatar>();
            avatar.IsOwnedLocally = true;
            Wear(avatar);
            CilboxProxy avatarScript = Proxy(avatarBox);
            for (int i = 0; i < BasisMenuShim.MaxSettingsTabs; i++)
            {
                Assert.IsNotNull(Running(avatarBox, () => BasisMenuShim.AddSettingsTab(avatarScript, "Avatar " + i)), "avatar tab " + i);
            }
            Assert.IsNotNull(Running(avatarBox, () => BasisMenuShim.AddPanel(avatarScript, "Avatar Panel")));

            CilboxPropBasis propBox = Box<CilboxPropBasis>();
            Local(propBox.gameObject.AddComponent<BasisProp>());
            CilboxProxy propScript = Proxy(propBox);
            Assert.IsNotNull(Running(propBox, () => BasisMenuShim.AddPanel(propScript, "Prop Panel")), "the world's boxes cannot use up the panel slots either");
        }

        [Test]
        public void APress_OpensTheMenuOnce()
        {
            CilboxPropBasis box = Box<CilboxPropBasis>();
            BasisProp prop = Local(box.gameObject.AddComponent<BasisProp>());
            CilboxPropBasis otherBox = Box<CilboxPropBasis>();
            BasisProp other = Local(otherBox.gameObject.AddComponent<BasisProp>());
            Assert.IsFalse((bool)Call(Menus, "Pressed", prop), "nothing was pressed yet");
            BasisUserActivation.Note(box.gameObject);
            Assert.IsFalse((bool)Call(Menus, "Pressed", other), "a press belongs to the content that was pressed");
            Assert.IsTrue((bool)Call(Menus, "Pressed", prop));
            Assert.IsFalse((bool)Call(Menus, "Pressed", prop), "the same press cannot open the menu again, so a script cannot reopen it every frame");
            System.Threading.Thread.Sleep(50);
            BasisUserActivation.Note(box.gameObject);
            Assert.IsTrue((bool)Call(Menus, "Pressed", prop), "the next press opens it again");
        }

        [Test]
        public void TextChanges_SkipRepeats_AndWaitForTheThrottledLayoutPass()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>();
            CilboxProxy script = Proxy(box);
            BasisMenuPage page = Running(box, () => BasisMenuShim.AddSettingsTab(script, "Laps"));
            BasisMenuItem label = Running(box, () => page.AddLabel("Lap 1"));
            PanelTabGroup group = PanelTabGroup.CreateNew(Canvas(), LayoutDirection.Vertical);
            Assert.IsNotNull(group, "the tab group prefab did not load");
            PanelTabPage tab = (PanelTabPage)Call(View, "BuildTab", State(page), group);
            Assert.IsNotNull(tab, "the tab page prefab did not load");
            object state = State(page);

            Running(box, () => label.SetText("Lap 1"));
            Assert.IsFalse((bool)Member(state, "Layout"), "setting the same text again does nothing");
            Running(box, () => label.SetText("Lap 2"));
            Assert.IsTrue(Shows(tab, "Lap 2"), "the text itself changes at once");
            Assert.IsTrue((bool)Member(state, "Layout"), "the layout pass waits for the tick instead of running on every call");
            SetMember(state, "NextBuild", double.MaxValue);
            Call(Menus, "Tick");
            Assert.IsTrue((bool)Member(state, "Layout"), "and keeps to the rebuild throttle");
            SetMember(state, "NextBuild", 0d);
            Call(Menus, "Tick");
            Assert.IsFalse((bool)Member(state, "Layout"));
            Running(box, () => label.SetVisible(false));
            Assert.IsTrue((bool)Member(state, "Layout"), "hiding a row waits for the same pass");
        }

        [Test]
        public void SettingsTabsChurnedWhileSettingsIsOpen_ReuseOneSlot()
        {
            PanelTabGroup group = PanelTabGroup.CreateNew(Canvas(), LayoutDirection.Vertical);
            Assert.IsNotNull(group, "the tab group prefab did not load");
            SettingsOpen(group);
            CilboxSceneBasis box = Box<CilboxSceneBasis>();
            CilboxProxy script = Proxy(box);
            Assert.IsNotNull(Running(box, () => BasisMenuShim.AddSettingsTab(script, "Keep")));
            PanelButton reused = null;
            for (int i = 0; i < 12; i++)
            {
                BasisMenuPage churn = Running(box, () => BasisMenuShim.AddSettingsTab(script, "Churn " + i));
                Assert.IsNotNull(churn, "round " + i);
                Assert.AreEqual(2, group.SelectionButtons.Count, "round " + i + " grew the tab strip");
                Assert.AreEqual(2, group.Pages.Count, "round " + i + " grew the page list");
                PanelButton button = group.SelectionButtons[1];
                if (reused == null) reused = button;
                Assert.AreSame(reused, button, "a removed tab's button is reused");
                Assert.AreEqual("Churn " + i, button.Descriptor.Title);
                Assert.IsTrue(button.gameObject.activeSelf);
                Assert.IsFalse(group.Pages[1].gameObject.activeSelf, "a tab added behind the shown one stays hidden");
                Running(box, () => churn.Remove());
                Assert.IsFalse(button.gameObject.activeSelf);
                Assert.IsTrue(group.Pages[1] == null, "a removed tab's page is released");
            }
            Assert.AreEqual(1, group.Descriptor.ContentParent.GetComponentsInChildren<PanelTabPage>(true).Length, "only the kept tab's page is left");
        }

        [Test]
        public void PanelTabsChurnedWhileThePanelIsOpen_ReuseOneSlot()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>();
            CilboxProxy script = Proxy(box);
            BasisMenuPage home = Running(box, () => BasisMenuShim.AddPanel(script, "Arcade"));
            object panel = Member(State(home), "Panel");
            PanelTabGroup group = PanelTabGroup.CreateNew(Canvas(), LayoutDirection.Vertical);
            Assert.IsNotNull(group, "the tab group prefab did not load");
            SetMember(panel, "Group", group);
            Call(View, "AddTab", panel, State(home));
            PanelButton reused = null;
            for (int i = 0; i < 12; i++)
            {
                BasisMenuPage round = Running(box, () => home.AddTab("Round " + i));
                Assert.IsNotNull(round, "round " + i);
                object state = State(round);
                Call(View, "AddTab", panel, state);
                Assert.AreEqual(1, (int)Member(state, "Tab"), "round " + i);
                Assert.AreEqual(2, group.SelectionButtons.Count, "round " + i + " grew the tab strip");
                PanelButton button = group.SelectionButtons[1];
                if (reused == null) reused = button;
                Assert.AreSame(reused, button, "a removed tab's button is reused");
                Assert.AreEqual("Round " + i, button.Descriptor.Title);
                Assert.IsFalse(group.Pages[1].gameObject.activeSelf, "appending a tab does not show it over the open one");
                Running(box, () => round.Remove());
                Call(View, "Release", state);
                Assert.IsTrue(group.Pages[1] == null, "a removed tab's page is released");
            }
            Assert.AreEqual(0, group.Value, "the open tab never moved");
        }

        [Test]
        public void AddingTabs_IsRateLimitedPerBox()
        {
            int budget = (int)Constant("MaxAddsPerWindow");
            double window = (double)Constant("AddWindowSeconds");
            CilboxSceneBasis box = Box<CilboxSceneBasis>();
            CilboxProxy script = Proxy(box);
            int added = 0;
            for (int i = 0; i < budget * 2; i++)
            {
                BasisMenuPage page = Running(box, () => BasisMenuShim.AddSettingsTab(script, "Spin " + i));
                if (page == null) break;
                added++;
                Running(box, () => page.Remove());
            }
            Assert.AreEqual(budget, added, "a script that adds and removes a tab in a loop runs out of adds");
            CilboxSceneBasis other = Box<CilboxSceneBasis>();
            CilboxProxy otherScript = Proxy(other);
            Assert.IsNotNull(Running(other, () => BasisMenuShim.AddSettingsTab(otherScript, "Other Box")), "the budget belongs to the box");
            FieldInfo start = typeof(CilboxBasisCommon).GetField("menuWindowStart", Fields);
            Assert.IsNotNull(start, "CilboxBasisCommon.menuWindowStart moved; this test cannot age the window.");
            start.SetValue(box, Time.realtimeSinceStartupAsDouble - window - 1d);
            Assert.IsNotNull(Running(box, () => BasisMenuShim.AddSettingsTab(script, "Later")), "and comes back once the window has passed");
        }

        [Test]
        public void Clean_BreaksEveryTextMeshProEscape_AndKeepsTheBackslashReadable()
        {
            string attack = "\\u003Csize=300%\\u003E\\U0000003C\\n\\\\u003C" + Joiner + "\\u003C";
            foreach (bool multiline in new[] { false, true })
            {
                string clean = (string)Call(Menus, "Clean", attack, 512, multiline);
                NoEscapes(clean);
                StringAssert.Contains("u003Csize=300%", clean, "the text stays readable");
            }
            string profiled = (string)Call(Profiler, "Clean", attack, 512);
            NoEscapes(profiled);
            StringAssert.Contains("u003Csize=300%", profiled);
            Assert.AreEqual("ab", Call(Menus, "Clean", "a" + Joiner + "b", 48, false), "a joiner in the input goes with the other format characters");
            string capped = (string)Call(Menus, "Clean", new string('\\', 100), 48, false);
            Assert.LessOrEqual(capped.Length, 48);
            NoEscapes(capped);
            Assert.AreEqual("[b]Big[/b]", Call(Profiler, "Clean", "<b>Big</b>", 40));
        }

        [Test]
        public void TextMeshPro_ShowsACleanedEscapeAsTypedText_AndTheJoinerTakesNoRoom()
        {
            PanelElementDescriptor card = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, Canvas());
            Assume.That(card, Is.Not.Null, "the card prefab did not load");
            card.DisableRichText();
            PropertyInfo title = typeof(PanelElementDescriptor).GetProperty("TitleLabel");
            Assert.IsNotNull(title, "PanelElementDescriptor.TitleLabel moved; this test cannot reach the label.");
            object label = title.GetValue(card);
            Assume.That(label, Is.Not.Null, "the card has no title label");
            string attack = "\\u003Cb\\u003EHi";
            Assume.That(Shown(label, attack), Does.Contain("<"), "this TextMeshPro no longer decodes backslash escapes, so there is nothing to break");
            string shown = Shown(label, (string)Call(Menus, "Clean", attack, 48, false));
            Assert.That(shown, Does.Not.Contain("<"));
            StringAssert.Contains("u003Cb", shown, "the escape shows as the text that was typed");
            float plain = Width(label, "xx");
            Assume.That(plain, Is.GreaterThan(0f), "TextMeshPro measured nothing, so widths cannot be compared");
            Assert.AreEqual(plain, Width(label, "x" + Joiner + "x"), 0.01f, "the joiner draws nothing");
        }
    }
}
