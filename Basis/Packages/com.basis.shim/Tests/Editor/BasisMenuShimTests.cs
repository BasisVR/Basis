using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisMenuShimTests
    {
        const string Probe = "ZZMenuProbe";
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        static readonly Assembly Shims = typeof(CilboxBasisCommon).Assembly;
        static readonly Type Menus = Shims.GetType("Cilbox.CilboxMenus", true);
        static readonly Type View = Shims.GetType("Cilbox.CilboxMenuView", true);
        static readonly PropertyInfo LocalPlayer = typeof(BasisLocalPlayer).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        readonly List<GameObject> hosts = new List<GameObject>();
        BasisLocalPlayer savedPlayer;

        [SetUp]
        public void SetUp()
        {
            savedPlayer = BasisLocalPlayer.Instance;
            BasisAddressablePrefabCache.ResetForTests(path => UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path));
        }

        [TearDown]
        public void TearDown()
        {
            Call(Menus, "Reset");
            SettingsOpen(null);
            LocalPlayer.SetValue(null, savedPlayer);
            LogAssert.ignoreFailingMessages = false;
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
            Assert.Fail($"{type.Name}.{name} moved; this test cannot drive the menus.");
            return null;
        }

        static object Member(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{target.GetType().Name}.{name} moved; this test cannot read the menus.");
            return field.GetValue(target);
        }

        static object State(BasisMenuPage page) => Call(Menus, "Find", page);

        static object Field(string name)
        {
            FieldInfo field = typeof(SettingsProvider).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, $"SettingsProvider.{name} moved; this test cannot open Settings.");
            return field;
        }

        static void SettingsOpen(PanelTabGroup group)
        {
            ((FieldInfo)Field("_searchTabGroup")).SetValue(null, group);
            ((IDictionary)((FieldInfo)Field("_tabKeyToIndex")).GetValue(null)).Clear();
            ((IList)((FieldInfo)Field("_lazyTabs")).GetValue(null)).Clear();
            ((IList)((FieldInfo)Field("_tabSearches")).GetValue(null)).Clear();
        }

        T Box<T>(bool script) where T : CilboxBasisCommon
        {
            GameObject host = new GameObject(typeof(T).Name + " menu test");
            hosts.Add(host);
            T box = host.AddComponent<T>();
            if (script)
            {
                box.assemblyData = BuildAssembly();
                box.BoxInitialize();
            }
            else box.classes = new Dictionary<string, int>();
            return box;
        }

        static CilboxProxy Proxy(CilboxBasisCommon box)
        {
            CilboxProxy proxy = box.gameObject.AddComponent<CilboxProxy>();
            proxy.box = box;
            if (box.classes.Count == 0) return proxy;
            proxy.cls = box.GetClass(Probe);
            proxy.fields = new StackElement[4];
            proxy.fields[0].LoadInt(0);
            proxy.fields[1].LoadBool(false);
            proxy.fields[2].LoadFloat(0f);
            proxy.fields[3].LoadInt(0);
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

        static T Callback<T>(CilboxProxy proxy, string method) where T : Delegate
        {
            CilboxClass cls = proxy.cls;
            return (T)CilboxPlatform.ProxyForGeneratingActions<T>(proxy, cls.methods[cls.methodNameToIndex[method]]);
        }

        RectTransform Canvas()
        {
            GameObject canvas = new GameObject("Menu test canvas", typeof(Canvas));
            hosts.Add(canvas);
            canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(1920, 1080);
            RectTransform host = (RectTransform)new GameObject("Host", typeof(RectTransform)).transform;
            host.SetParent(canvas.transform, false);
            host.sizeDelta = new Vector2(1200, 1000);
            return host;
        }

        PanelTabPage Built(BasisMenuPage page)
        {
            PanelTabGroup group = PanelTabGroup.CreateNew(Canvas(), LayoutDirection.Vertical);
            Assert.IsNotNull(group, "the tab group prefab did not load");
            PanelTabPage tab = (PanelTabPage)Call(View, "BuildTab", State(page), group);
            Assert.IsNotNull(tab, "the tab page prefab did not load");
            return tab;
        }

        static void Tick() => Call(Menus, "Tick");

        static void Due(BasisMenuPage page)
        {
            object state = State(page);
            state.GetType().GetField("NextBuild", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(state, 0d);
        }

        static int ClockRequests()
        {
            FieldInfo field = typeof(Basis.Scripts.Drivers.BasisFrameClock).GetField("_requestCount", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "BasisFrameClock._requestCount moved; this test cannot see whether the menus keep ticking.");
            return (int)field.GetValue(null);
        }

        static T Titled<T>(Component root, string title) where T : PanelComponent
        {
            foreach (T control in root.GetComponentsInChildren<T>(true))
            {
                if (control.Descriptor != null && control.Descriptor.Title == title) return control;
            }
            return null;
        }

        static bool Method(CilboxBasisCommon box, Type type, string name) =>
            box.CheckTypeAllowed(type) &&
            box.CheckMethodAllowed(out MethodInfo _, type, name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), name + "()");

        static string BuildAssembly()
        {
            SerializedTypeDescriptor probe = new SerializedTypeDescriptor { assemblyName = "Assembly-CSharp", typeName = Probe };
            SerializedTypeDescriptor int32 = SerializedTypeDescriptorBuilder.FromNativeType(typeof(int));
            SerializedTypeDescriptor boolean = SerializedTypeDescriptorBuilder.FromNativeType(typeof(bool));
            SerializedTypeDescriptor single = SerializedTypeDescriptorBuilder.FromNativeType(typeof(float));
            const byte clicks = 1, flag = 2, amount = 3, pick = 4, title = 5, addTab = 6, text = 7, addLabel = 8;
            SerializedAssembly assembly = new SerializedAssembly
            {
                classes = new[]
                {
                    new SerializedClass
                    {
                        className = Probe,
                        staticFields = Array.Empty<SerializedField>(),
                        instanceFields = new[]
                        {
                            new SerializedField { name = "clicks", type = int32 },
                            new SerializedField { name = "flag", type = boolean },
                            new SerializedField { name = "amount", type = single },
                            new SerializedField { name = "pick", type = int32 },
                        },
                        baseClassNames = Array.Empty<string>(),
                        methods = new[]
                        {
                            ScriptMethod("OnClick", "Void OnClick()", 3, new byte[] { 0x02, 0x02, 0x7B, clicks, 0, 0, 0, 0x17, 0x58, 0x7D, clicks, 0, 0, 0, 0x2A }),
                            ScriptMethod("OnToggle", "Void OnToggle(Boolean)", 2, new byte[] { 0x02, 0x03, 0x7D, flag, 0, 0, 0, 0x2A }, new SerializedField { name = "on", type = boolean }),
                            ScriptMethod("OnSlide", "Void OnSlide(Single)", 2, new byte[] { 0x02, 0x03, 0x7D, amount, 0, 0, 0, 0x2A }, new SerializedField { name = "value", type = single }),
                            ScriptMethod("OnPick", "Void OnPick(Int32)", 2, new byte[] { 0x02, 0x03, 0x7D, pick, 0, 0, 0, 0x2A }, new SerializedField { name = "index", type = int32 }),
                            ScriptMethod("Thrower", "Void Thrower()", 1, new byte[] { 0x14, 0x7B, clicks, 0, 0, 0, 0x26, 0x2A }),
                            ScriptMethod("AddMenus", "Void AddMenus()", 3, new byte[] { 0x02, 0x72, title, 0, 0, 0, 0x28, addTab, 0, 0, 0, 0x72, text, 0, 0, 0, 0x6F, addLabel, 0, 0, 0, 0x26, 0x2A }),
                        },
                    },
                },
                metadata = new[]
                {
                    FieldToken(clicks, probe, "clicks", 0),
                    FieldToken(flag, probe, "flag", 1),
                    FieldToken(amount, probe, "amount", 2),
                    FieldToken(pick, probe, "pick", 3),
                    new SerializedMetadataToken { metaTokenIndex = title, metaTokenType = (byte)MetaTokenType.mtString, stringValue = "From Script" },
                    NativeToken(addTab, typeof(BasisMenuShim), nameof(BasisMenuShim.AddSettingsTab), true, typeof(object), typeof(string)),
                    new SerializedMetadataToken { metaTokenIndex = text, metaTokenType = (byte)MetaTokenType.mtString, stringValue = "Hi" },
                    NativeToken(addLabel, typeof(BasisMenuPage), nameof(BasisMenuPage.AddLabel), false, typeof(string)),
                },
                enums = Array.Empty<SerializedEnum>(),
            };
            return assembly.SerializeString();
        }

        static SerializedMetadataToken FieldToken(int index, SerializedTypeDescriptor owner, string name, int field)
        {
            return new SerializedMetadataToken { metaTokenIndex = index, metaTokenType = (byte)MetaTokenType.mtField, typeDescriptor = owner, name = name, fieldHasIndex = true, fieldIndex = field };
        }

        static SerializedMetadataToken NativeToken(int index, Type owner, string name, bool isStatic, params Type[] parameters)
        {
            SerializedTypeDescriptor[] descriptors = new SerializedTypeDescriptor[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) descriptors[i] = SerializedTypeDescriptorBuilder.FromNativeType(parameters[i]);
            return new SerializedMetadataToken
            {
                metaTokenIndex = index,
                metaTokenType = (byte)MetaTokenType.mtMethod,
                typeDescriptor = SerializedTypeDescriptorBuilder.FromNativeType(owner),
                name = name,
                methodFullSignature = owner.GetMethod(name, parameters).ToString(),
                methodAssembly = owner.Assembly.GetName().Name,
                isStatic = isStatic,
                methodParameters = descriptors,
                methodGenericArguments = Array.Empty<SerializedTypeDescriptor>(),
            };
        }

        static SerializedMethod ScriptMethod(string name, string signature, int stack, byte[] body, params SerializedField[] parameters)
        {
            return new SerializedMethod
            {
                methodName = name,
                fullSignature = signature,
                maxStack = stack,
                isVoid = true,
                body = body,
                locals = Array.Empty<SerializedField>(),
                parameters = parameters,
                exceptionHandlers = Array.Empty<SerializedExceptionHandler>(),
            };
        }

        [Test]
        public void TheEntryPoints_ResolveInEveryBox_AndNothingElseOnTheMenuTypesDoes()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                GameObject host = new GameObject(boxType.Name + " gate test");
                hosts.Add(host);
                CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
                box.classes = new Dictionary<string, int>();
                Assert.IsTrue(Method(box, typeof(BasisMenuShim), nameof(BasisMenuShim.AddSettingsTab)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisMenuShim), nameof(BasisMenuShim.AddPanel)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisMenuPage), nameof(BasisMenuPage.AddToggle)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisMenuPage), "get_" + nameof(BasisMenuPage.IsOpen)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisMenuItem), nameof(BasisMenuItem.SetValue)), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisMenuPage), ".ctor"), boxType.Name + " must not construct its own page handle.");
                Assert.IsFalse(Method(box, typeof(BasisMenuItem), ".ctor"), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisMenuShim), "Find"), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisMenuItem), "Changed"), boxType.Name);
            }
        }

        [Test]
        public void AScript_AddsItsOwnTab_ThroughTheInterpreter()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>(true);
            CilboxProxy proxy = Proxy(box);
            Assert.AreEqual(0, (int)Call(Menus, "get_PageCount"));

            proxy.cls.InterpretMethod(proxy, "AddMenus", null);

            Assert.IsFalse(proxy.disabled, "The script must not have faulted.");
            Assert.AreEqual(1, (int)Call(Menus, "get_PageCount"), "The script's own call must reach the shim while its box is running.");
            object state = ((IList)Menus.GetField("pages", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null))[0];
            Assert.AreEqual("From Script", Member(state, "Title"));
            Assert.IsTrue(SettingsProvider.HasContentTab((string)Member(state, "Key")));
            IList items = (IList)Member(state, "Items");
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual("Hi", Member(items[0], "Text"));
        }

        [Test]
        public void OnlyARunningScript_GetsAPage()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>(false);
            CilboxProxy proxy = Proxy(box);

            Assert.IsNull(BasisMenuShim.AddSettingsTab(proxy, "Idle"), "Nothing may be added while the box is not running.");
            Assert.IsNull(Running(box, () => BasisMenuShim.AddSettingsTab(box.gameObject, "Not a script")));
            Assert.IsNull(Running(box, () => BasisMenuShim.AddSettingsTab(null, "Nobody")));
            BasisMenuPage page = Running(box, () => BasisMenuShim.AddSettingsTab(proxy, "World Tab"));
            Assert.IsNotNull(page);
            Assert.IsTrue(page.IsValid);
            Assert.IsTrue(SettingsProvider.HasContentTab((string)Member(State(page), "Key")));
            Assert.IsNull(page.AddLabel("Not running"), "A handle only works while its own box is running.");
            Assert.IsNotNull(Running(box, () => page.AddLabel("Running")));
        }

        [Test]
        public void PropsAndAvatars_OnlyForTheLocalPlayer()
        {
            CilboxPropBasis loose = Box<CilboxPropBasis>(false);
            Assert.IsNull(Running(loose, () => BasisMenuShim.AddPanel(Proxy(loose), "No Content")), "A prop that resolves to no content is refused.");

            CilboxPropBasis prop = Box<CilboxPropBasis>(false);
            BasisProp content = prop.gameObject.AddComponent<BasisProp>();
            CilboxProxy propScript = Proxy(prop);
            Assert.IsNull(Running(prop, () => BasisMenuShim.AddSettingsTab(propScript, "Their Prop")), "Another player's prop must not reach this player's menu.");
            content.AssignContentIdentifier(new BasisNetworkContentBase.BasisContentInformation { LoadedNetID = "menu-test", SpawnedByLocalPlayer = true });
            Assert.IsNotNull(Running(prop, () => BasisMenuShim.AddSettingsTab(propScript, "My Prop")));

            CilboxAvatarBasis avatarBox = Box<CilboxAvatarBasis>(false);
            BasisAvatar avatar = avatarBox.gameObject.AddComponent<BasisAvatar>();
            CilboxProxy avatarScript = Proxy(avatarBox);
            avatar.IsOwnedLocally = false;
            Assert.IsNull(Running(avatarBox, () => BasisMenuShim.AddPanel(avatarScript, "Their Avatar")), "A remote avatar runs on this client too and must be refused.");
            avatar.IsOwnedLocally = true;
            Assert.IsNull(Running(avatarBox, () => BasisMenuShim.AddPanel(avatarScript, "Claims To Be Mine")), "Any avatar script can set IsOwnedLocally, so it must not grant access.");
            BasisLocalPlayer player = (BasisLocalPlayer)FormatterServices.GetUninitializedObject(typeof(BasisLocalPlayer));
            player.BasisAvatar = avatar;
            LocalPlayer.SetValue(null, player);
            Assert.IsNotNull(Running(avatarBox, () => BasisMenuShim.AddPanel(avatarScript, "My Avatar")));
        }

        [Test]
        public void Limits_OnTabsPanelsItemsAndTitles()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>(false);
            CilboxProxy proxy = Proxy(box);
            for (int i = 0; i < BasisMenuShim.MaxSettingsTabs; i++)
            {
                Assert.IsNotNull(Running(box, () => BasisMenuShim.AddSettingsTab(proxy, "Tab " + i)), "tab " + i);
            }
            Assert.IsNull(Running(box, () => BasisMenuShim.AddSettingsTab(proxy, "One Too Many")));

            CilboxSceneBasis other = Box<CilboxSceneBasis>(false);
            CilboxProxy otherScript = Proxy(other);
            Assert.IsNull(Running(other, () => BasisMenuShim.AddSettingsTab(otherScript, "tab 0")), "Titles are unique across content, ignoring case.");
            Assert.IsNull(Running(other, () => BasisMenuShim.AddSettingsTab(otherScript, "   ")));

            BasisMenuPage panel = Running(box, () => BasisMenuShim.AddPanel(proxy, "Arcade"));
            Assert.IsNotNull(panel);
            Assert.IsNull(Running(box, () => BasisMenuShim.AddPanel(proxy, "Second Panel")), "One panel per content.");
            Assert.IsNull(Running(other, () => BasisMenuShim.AddPanel(otherScript, "ARCADE")), "Panel titles must not collide with any other menu button.");
            for (int i = 1; i < BasisMenuShim.MaxPanelTabs; i++)
            {
                Assert.IsNotNull(Running(box, () => panel.AddTab("Page " + i)), "panel tab " + i);
            }
            Assert.IsNull(Running(box, () => panel.AddTab("Page Too Many")));

            for (int i = 0; i < BasisMenuShim.MaxItems; i++)
            {
                Assert.IsNotNull(Running(box, () => panel.AddLabel("Line " + i)), "item " + i);
            }
            Assert.IsNull(Running(box, () => panel.AddLabel("Line Too Many")));
            Assert.IsNull(Running(box, () => panel.AddSlider("Backwards", 5f, 1f, 2f, null)));
            Assert.IsNull(Running(box, () => panel.AddSlider("No Whole Number", 0.2f, 0.8f, 0.5f, true, null)));
            Assert.IsNull(Running(box, () => panel.AddDropdown("Empty", new string[0], 0, null)));
        }

        [Test]
        public void Text_CannotCarryMarkupOrControlCharacters()
        {
            string rtl = ((char)0x202E).ToString();
            string clean = (string)Call(Menus, "Clean", "<size=500><b>Big</b>\n" + rtl + "txt", 48, false);
            Assert.IsFalse(clean.Contains("<") || clean.Contains(">"), clean);
            Assert.IsFalse(clean.Contains(rtl), "Direction overrides could disguise the text.");
            Assert.IsFalse(clean.Contains("\n"), "Titles stay on one line.");
            StringAssert.Contains("Big", clean);

            string label = (string)Call(Menus, "Clean", "one\ntwo\nthree", 512, true);
            Assert.AreEqual("one\ntwo\nthree", label, "Labels keep their line breaks.");
            Assert.LessOrEqual(((string)Call(Menus, "Clean", new string('x', 100), 48, false)).Length, 48);

            string emoji = new string('a', 47) + char.ConvertFromUtf32(0x1F600);
            string cut = (string)Call(Menus, "Clean", emoji, 48, false);
            Assert.IsFalse(char.IsHighSurrogate(cut[cut.Length - 1]), "A cut must not leave half a surrogate pair.");
        }

        [Test]
        public void TheSourceLines_AreInEveryLanguage_AndNameTheContent()
        {
            int files = 0;
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:TextAsset", new[] { "Packages/com.basis.framework/BasisUI/Localization/Languages" }))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                files++;
                Basis.Localization.BasisLanguageTable table = JsonUtility.FromJson<Basis.Localization.BasisLanguageTable>(UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(path).text);
                foreach (string kind in new[] { "world", "prop", "avatar" })
                {
                    string key = "menu.content.source." + kind;
                    Basis.Localization.BasisLanguageEntry entry = table.entries.Find(e => e.key == key);
                    Assert.IsNotNull(entry, path + " is missing " + key);
                    StringAssert.Contains("{0}", entry.value, path + " " + key + " must name the content.");
                }
            }
            Assert.GreaterOrEqual(files, 16);
        }

        [Test]
        public void Callbacks_MustBeTheScriptsOwnMethods()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>(true);
            CilboxProxy proxy = Proxy(box);
            CilboxSceneBasis other = Box<CilboxSceneBasis>(true);
            CilboxProxy stranger = Proxy(other);
            BasisMenuPage page = Running(box, () => BasisMenuShim.AddSettingsTab(proxy, "Callbacks"));

            Assert.IsNull(Running(box, () => page.AddButton("Native", () => { })), "A host delegate must not be handed to the menu.");
            Assert.IsNull(Running(box, () => page.AddButton("Stranger", Callback<Action>(stranger, "OnClick"))), "Another box's method must not be bound to this page.");
            Assert.IsNotNull(Running(box, () => page.AddButton("Own", Callback<Action>(proxy, "OnClick"))));
            Assert.IsNotNull(Running(box, () => page.AddButton("None", null)));
        }

        [Test]
        public void TheSettingsTab_BuildsNativeControls_AndRoutesChangesIntoTheScript()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>(true);
            CilboxProxy proxy = Proxy(box);
            BasisMenuPage page = Running(box, () => BasisMenuShim.AddSettingsTab(proxy, "Race Track"));
            BasisMenuItem header = Running(box, () => page.AddHeader("Race"));
            BasisMenuItem toggleItem = Running(box, () => page.AddToggle("Ghost car", false, Callback<Action<bool>>(proxy, "OnToggle")));
            BasisMenuItem sliderItem = Running(box, () => page.AddSlider("Laps", 0f, 10f, 3f, Callback<Action<float>>(proxy, "OnSlide")));
            BasisMenuItem dropdownItem = Running(box, () => page.AddDropdown("Weather", new[] { "Sun", "Rain", "Snow" }, 1, Callback<Action<int>>(proxy, "OnPick")));
            Running(box, () => page.AddButton("Reset", Callback<Action>(proxy, "OnClick")));
            BasisMenuItem label = Running(box, () => page.AddLabel("Lap 1 of 3"));
            Assert.IsNotNull(header);

            PanelTabPage tab = Built(page);
            PanelToggle toggle = Titled<PanelToggle>(tab, "Ghost car");
            PanelSlider slider = Titled<PanelSlider>(tab, "Laps");
            PanelDropdown dropdown = Titled<PanelDropdown>(tab, "Weather");
            PanelButton button = Titled<PanelButton>(tab, "Reset");
            Assert.IsNotNull(toggle);
            Assert.IsNotNull(slider);
            Assert.IsNotNull(dropdown);
            Assert.IsNotNull(button);
            Assert.AreEqual(3f, slider.Value);
            Assert.AreEqual(1, dropdown.Index);
            string source = (string)Member(State(page), "Source");
            string line = (string)Call(View, "SourceLine", State(page));
            Assert.IsFalse(string.IsNullOrEmpty(source));
            bool attributed = false, headed = false, labelled = false;
            foreach (PanelElementDescriptor element in tab.GetComponentsInChildren<PanelElementDescriptor>(true))
            {
                attributed |= element.Description == line;
                headed |= element.Title == "Race";
                labelled |= element.Description == "Lap 1 of 3";
            }
            Assert.IsTrue(attributed, "The page must say which content added it.");
            Assert.IsTrue(headed);
            Assert.IsTrue(labelled);

            toggle.SetValue(true);
            Assert.IsTrue(proxy.fields[1].b, "The toggle must reach the script.");
            Assert.AreEqual(1f, toggleItem.Value);
            slider.SetValue(7.5f);
            Assert.AreEqual(7.5f, proxy.fields[2].f);
            dropdown.SetValue("2");
            Assert.AreEqual(2, proxy.fields[3].i);
            button.OnClicked.Invoke();
            button.OnClicked.Invoke();
            Assert.AreEqual(2, proxy.fields[0].i);

            Running(box, () => sliderItem.SetValue(99f));
            Assert.AreEqual(10f, slider.Value, "Script writes are clamped to the range.");
            Assert.AreEqual(7.5f, proxy.fields[2].f, "A script write must not call the script back.");
            Running(box, () => dropdownItem.SetValue(0f));
            Assert.AreEqual(0, dropdown.Index);
            Running(box, () => label.SetText("Lap 2 of 3"));
            bool relabelled = false;
            foreach (PanelElementDescriptor element in tab.GetComponentsInChildren<PanelElementDescriptor>(true)) relabelled |= element.Description == "Lap 2 of 3";
            Assert.IsTrue(relabelled);
            Running(box, () => toggleItem.SetVisible(false));
            Assert.IsFalse(toggle.gameObject.activeSelf);
        }

        [Test]
        public void ChangesWhileShown_RebuildOnTheNextTick_AndTheTabGoesWithTheScript()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>(true);
            CilboxProxy proxy = Proxy(box);
            int requests = ClockRequests();
            BasisMenuPage page = Running(box, () => BasisMenuShim.AddSettingsTab(proxy, "Live"));
            Assert.AreEqual(requests + 1, ClockRequests(), "The frame clock only ticks while someone asks for it, and cleanup rides on that tick.");
            BasisMenuItem first = Running(box, () => page.AddToggle("First", true, null));
            PanelTabPage tab = Built(page);
            Assert.AreEqual(1, tab.GetComponentsInChildren<PanelToggle>(true).Length);

            Running(box, () => page.AddToggle("Second", false, null));
            Tick();
            Assert.AreEqual(2, tab.GetComponentsInChildren<PanelToggle>(true).Length);
            Running(box, () => first.Remove());
            Assert.IsFalse(first.IsValid);
            Tick();
            Assert.AreEqual(2, tab.GetComponentsInChildren<PanelToggle>(true).Length, "Rebuilds are spaced out, so a script churning items cannot rebuild the page every frame.");
            Due(page);
            Tick();
            Assert.AreEqual(1, tab.GetComponentsInChildren<PanelToggle>(true).Length, "A removed item is gone after the next rebuild.");

            string key = (string)Member(State(page), "Key");
            UnityEngine.Object.DestroyImmediate(proxy);
            Tick();
            Assert.IsFalse(page.IsValid);
            Assert.IsFalse(SettingsProvider.HasContentTab(key));
            Assert.AreEqual(0, (int)Call(Menus, "get_PageCount"));
            Assert.AreEqual(requests, ClockRequests(), "The request goes with the last page.");
        }

        [Test]
        public void AThrowingCallback_IsContained_AndItsTabGoesAway()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>(true);
            CilboxProxy proxy = Proxy(box);
            BasisMenuPage page = Running(box, () => BasisMenuShim.AddSettingsTab(proxy, "Faulty"));
            Running(box, () => page.AddButton("Break", Callback<Action>(proxy, "Thrower")));
            PanelButton button = Titled<PanelButton>(Built(page), "Break");
            Assert.IsNotNull(button);

            LogAssert.ignoreFailingMessages = true;
            Assert.DoesNotThrow(() => button.OnClicked.Invoke());
            Assert.IsTrue(proxy.disabled, "Cilbox disables the proxy that threw.");
            Tick();
            Assert.IsFalse(page.IsValid);
        }

        [Test]
        public void ATabAddedWhileSettingsIsOpen_AppearsAndGoesAway()
        {
            PanelTabGroup group = PanelTabGroup.CreateNew(Canvas(), LayoutDirection.Vertical);
            Assert.IsNotNull(group);
            SettingsOpen(group);
            CilboxSceneBasis box = Box<CilboxSceneBasis>(false);
            CilboxProxy proxy = Proxy(box);

            BasisMenuPage page = Running(box, () => BasisMenuShim.AddSettingsTab(proxy, "Race Track"));
            Running(box, () => page.AddToggle("Ghost car", true, null));
            Assert.AreEqual(1, group.SelectionButtons.Count);
            PanelButton button = group.SelectionButtons[0];
            Assert.AreEqual("Race Track", button.Descriptor.Title, "The tab shows the content's title, not a localization key.");

            string key = (string)Member(State(page), "Key");
            object lazy = null;
            foreach (object entry in (IList)((FieldInfo)Field("_lazyTabs")).GetValue(null))
            {
                if ((string)Member(entry, "Key") == key) lazy = entry;
            }
            Assert.IsNotNull(lazy);
            MethodInfo realize = typeof(SettingsProvider).GetMethod("RealizeTab", BindingFlags.NonPublic | BindingFlags.Static);
            PanelTabPage tab = (PanelTabPage)realize.Invoke(null, new[] { group, lazy, (object)true });
            Assert.IsNotNull(Titled<PanelToggle>(tab, "Ghost car"));

            Running(box, () => page.SetTitle("Renamed"));
            Assert.AreEqual("Renamed", button.Descriptor.Title);
            Running(box, () => page.SetVisible(false));
            Assert.IsFalse(button.gameObject.activeSelf);
            Running(box, () => page.SetVisible(true));
            Assert.IsTrue(button.gameObject.activeSelf);
            Running(box, () => page.Remove());
            Assert.IsFalse(button.gameObject.activeSelf);
            Assert.IsFalse(SettingsProvider.HasContentTab(key));
        }

        [Test]
        public void APanel_IsAMenuButton_UntilItsScriptGoes()
        {
            CilboxSceneBasis box = Box<CilboxSceneBasis>(false);
            CilboxProxy proxy = Proxy(box);
            BasisMenuPage page = Running(box, () => BasisMenuShim.AddPanel(proxy, "Arcade"));
            BasisMenuActionProvider<BasisMainMenu> provider = BasisMainMenu.FindProvider("Arcade");
            Assert.IsNotNull(provider);
            Assert.IsFalse(provider.Hidden);
            Assert.AreEqual(AddressableAssets.Sprites.World, provider.IconAddress);
            Assert.Greater(provider.Order, 90, "Content buttons sit after the built-in ones.");
            Assert.Less(provider.Order, 9999, "Exit stays last.");
            string place = ((IBasisMenuSharePlace)provider).DescribeSharePlace(null, out _);
            StringAssert.Contains(">", place, "Content panels opt out of setting shares.");

            Running(box, () => page.SetVisible(false));
            Assert.IsTrue(provider.Hidden, "A panel with nothing to show has no button.");
            BasisMenuPage rules = Running(box, () => page.AddTab("Rules"));
            Assert.IsNotNull(rules);
            Assert.IsFalse(provider.Hidden);
            Assert.IsFalse(Running(box, () => rules.Open()), "Open needs the player to have just used this content.");

            UnityEngine.Object.DestroyImmediate(proxy);
            Tick();
            Assert.IsNull(BasisMainMenu.FindProvider("Arcade"));
            Assert.AreEqual(0, (int)Call(Menus, "get_PanelCount"));
        }
    }
}
