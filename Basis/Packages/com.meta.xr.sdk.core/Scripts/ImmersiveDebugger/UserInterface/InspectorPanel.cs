/*
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * All rights reserved.
 *
 * Licensed under the Oculus SDK License Agreement (the "License");
 * you may not use the Oculus SDK except in compliance with the License,
 * which is provided at the time of installation or download, or which
 * otherwise accompanies this software in either electronic or hard copy form.
 *
 * You may obtain a copy of the License at
 *
 * https://developer.oculus.com/licenses/oculussdk/
 *
 * Unless required by applicable law or agreed to in writing, the Oculus SDK
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using Meta.XR.ImmersiveDebugger.Utils;
using System;
using System.Collections.Generic;
using Meta.XR.ImmersiveDebugger.Hierarchy;
using Meta.XR.ImmersiveDebugger.Manager;
using UnityEngine;

namespace Meta.XR.ImmersiveDebugger.UserInterface
{
    internal class InspectorPanel : DebugPanel, IDebugUIPanel
    {
        private ScrollView _scrollView;
        private Flex Flex => _scrollView.Flex;
        internal ScrollView ScrollView => _scrollView;

        private ScrollView _categoryScrollView;
        private Flex CategoryFlex => _categoryScrollView.Flex;

        private ScrollView _hierarchyScrollView;
        private Flex HierarchyFlex => _hierarchyScrollView.Flex;

        private readonly Dictionary<Category, Dictionary<Type, Dictionary<InstanceHandle, Inspector>>>
            _registries = new();

        private readonly Dictionary<Category, CategoryButton> _categories = new();
        private readonly Dictionary<Item, HierarchyItemButton> _items = new();

        private CategoryButton _selectedCategory;
        private HierarchyItemButton _selectedItem;

        private Background _categoryBackground;

        private Vector3 _currentPosition;
        private Vector3 _targetPosition;
        private readonly float _lerpSpeed = 10f;
        private bool _lerpCompleted = true;
        private ImageStyle _categoryBackgroundImageStyle;
        private DebugInterface _debugInterface;
        private Flex _buttonsAnchor;
        private Label _selectedModeTitle;
        private Toggle _hierarchyIcon;
        private Toggle _categoriesIcon;
        private Flex _categoryDiv;

        public ImageStyle CategoryBackgroundStyle
        {
            set
            {
                _categoryBackground.Sprite = value.sprite;
                _categoryBackground.Color = value.color;
                _categoryBackground.PixelDensityMultiplier = value.pixelDensityMultiplier;
            }
        }

        protected override void Setup(Controller owner)
        {
            base.Setup(owner);

#pragma warning disable CS0618 // Type or member is obsolete
            _debugInterface = FindObjectOfType<DebugInterface>();
#pragma warning restore CS0618 // Type or member is obsolete

            var div = Append<Flex>("div");
            div.LayoutStyle = Style.Load<LayoutStyle>("InspectorDivFlex");

            _categoryDiv = div.Append<Flex>("categories_div");
            _categoryDiv.LayoutStyle = Style.Load<LayoutStyle>("CategoriesDiv");

            // Left Tab Background
            _categoryBackground = _categoryDiv.Append<Background>("background");
            _categoryBackground.LayoutStyle = Style.Load<LayoutStyle>("CategoriesDivBackground");
            _categoryBackgroundImageStyle = Style.Load<ImageStyle>("CategoriesDivBackground");
            CategoryBackgroundStyle = _categoryBackgroundImageStyle;

            // Flex
            _buttonsAnchor = _categoryDiv.Append<Flex>("header");
            _buttonsAnchor.LayoutStyle = Style.Load<LayoutStyle>("ConsoleButtons");

            // Add Icons to switch mode
            _hierarchyIcon = RegisterControl("Hierarchy",
                Resources.Load<Texture2D>("Textures/hierarchy_icon"),
                Style.Load<ImageStyle>("InspectorModeIcon"),
                SelectHierarchyMode);
            _categoriesIcon = RegisterControl("Categories",
                Resources.Load<Texture2D>("Textures/categories_icon"),
                Style.Load<ImageStyle>("InspectorModeIcon"),
                SelectCategoryMode);

            // Test button
            _selectedModeTitle = _buttonsAnchor.Append<Label>("title");
            _selectedModeTitle.LayoutStyle = Style.Load<LayoutStyle>("InspectorModeTitle");
            _selectedModeTitle.TextStyle = Style.Load<TextStyle>("MemberTitle");

            // Category Scroll View
            _categoryScrollView = _categoryDiv.Append<ScrollView>("categories");
            _categoryScrollView.LayoutStyle = Style.Load<LayoutStyle>("CategoriesScrollView");
            CategoryFlex.LayoutStyle = Style.Load<LayoutStyle>("InspectorCategoryFlex");

            // Hierarchy Scroll View
            _hierarchyScrollView = _categoryDiv.Append<ScrollView>("categories");
            _hierarchyScrollView.LayoutStyle = Style.Load<LayoutStyle>("CategoriesScrollView");
            HierarchyFlex.LayoutStyle = Style.Load<LayoutStyle>("InspectorCategoryFlex");

            _scrollView = div.Append<ScrollView>("main");
            _scrollView.LayoutStyle = Style.Load<LayoutStyle>("PanelScrollView");
            Flex.LayoutStyle = Style.Load<LayoutStyle>("InspectorMainFlex");

            SelectCategoryMode();
        }

        private Toggle RegisterControl(string buttonName, Texture2D icon, ImageStyle style, Action callback)
        {
            if (buttonName == null) throw new ArgumentNullException(nameof(buttonName));
            if (icon == null) throw new ArgumentNullException(nameof(icon));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var toggle = _buttonsAnchor.Append<Toggle>(buttonName);
            toggle.LayoutStyle = Style.Load<LayoutStyle>("ConsoleButton");
            toggle.Icon = icon;
            toggle.IconStyle = style ? style : Style.Default<ImageStyle>();
            toggle.Callback = callback;
            return toggle;
        }

        private void SelectCategoryMode()
        {
            _selectedModeTitle.Content = "Custom Inspectors";
            _categoryDiv.Forget(_hierarchyScrollView);
            _categoryDiv.Remember(_categoryScrollView);
            _categoriesIcon.State = true;
            _hierarchyIcon.State = false;
        }

        private void SelectHierarchyMode()
        {
            _selectedModeTitle.Content = "Hierarchy View";
            _categoryDiv.Forget(_categoryScrollView);
            _categoryDiv.Remember(_hierarchyScrollView);
            _hierarchyIcon.State = true;
            _categoriesIcon.State = false;
        }

        protected override void OnTransparencyChanged()
        {
            base.OnTransparencyChanged();
            _categoryBackground.Color = Transparent ? _categoryBackgroundImageStyle.colorOff : _categoryBackgroundImageStyle.color;
        }

        public IInspector RegisterInspector(InstanceHandle instanceHandle, Category category)
        {
            if (instanceHandle.Instance != null && instanceHandle.Instance is not Component)
            {
                // Special case for when the instance is actually not a component
                // This can happen when it's the GameObject or a Scene, coming from the hierarchy view.
                // In this case, we want to still create the hierarchy item button if needed,
                // but we actually won't register an inspector.
                var button = GetHierarchyItemButton(category.Item, true);
                button.Counter++;
                return null;
            }

            var inspector = GetInspectorInternal(instanceHandle, category, true, out var registry);
            if (inspector != null) return inspector;

            var previousScroll = _scrollView.Progress;

            var instance = instanceHandle.Instance;
            var inspectorName = instance != null ? instance.name : instanceHandle.Type.Name;
            inspector = Flex.Append<Inspector>(inspectorName);
            inspector.LayoutStyle = Style.Load<LayoutStyle>("Inspector");
            inspector.InstanceHandle = instanceHandle;

            registry.Add(instanceHandle, inspector);

            _scrollView.Progress = previousScroll;

            if (category.Item != null)
            {
                // In case of a hierarchy component, we prefer to fold it
                inspector.Foldout.State = false;

                // Hierarchy behaviour
                var button = GetHierarchyItemButton(category.Item, true);
                button.Counter++;

                // If this inspector should not be seen right now
                if (!_hierarchyScrollView.Visibility || _selectedItem != button)
                {
                    Flex.Forget(inspector);
                }
            }
            else
            {
                var button = GetCategoryButton(category, true);
                button.Counter++;

                // If this inspector should not be seen right now
                if (!_categoryScrollView.Visibility || _selectedCategory != button)
                {
                    Flex.Forget(inspector);
                }
            }

            return inspector;
        }

        public void UnregisterInspector(InstanceHandle instanceHandle, Category category, bool allCategories)
        {
            if (allCategories)
            {
                foreach (var (otherCategory, categoryRegistry) in _registries)
                {
                    if (!categoryRegistry.TryGetValue(instanceHandle.Type, out var typeRegistry)) continue;
                    if (!typeRegistry.TryGetValue(instanceHandle, out var inspector)) continue;

                    typeRegistry.Remove(instanceHandle);

                    RemoveInspector(otherCategory, inspector);
                }
            }
            else
            {
                if (instanceHandle.Instance is not Component)
                {
                    // Special case for when the instance is actually not a component
                    // This can happen when it's the GameObject or a Scene, coming from the hierarchy view.
                    // In this case, we don't have an inspector to remove
                    // but we still want to remove the hierarchy item button if necessary.
                    TryRemoveHierarchyItemButton(category.Item);
                    return;
                }

                var inspector = GetInspectorInternal(instanceHandle, category, false, out var registry); ;
                if (inspector == null) return;

                // Unregister the inspector
                registry?.Remove(instanceHandle);

                RemoveInspector(category, inspector);
            }
        }

        private void RemoveInspector(Category category, Inspector inspector)
        {
            var previousScroll = _scrollView.Progress;

            // Destroy the inspector
            Flex.Remove(inspector, true);

            _scrollView.Progress = previousScroll;

            if (category.Item != null)
            {
                TryRemoveHierarchyItemButton(category.Item);
            }
            else
            {
                // Category Behaviour
                var button = GetCategoryButton(category, false);
                if (button != null)
                {
                    button.Counter--;
                }
            }
        }

        public IInspector GetInspector(InstanceHandle instanceHandle, Category category)
            => GetInspectorInternal(instanceHandle, category, false, out _);

        public Inspector GetInspectorInternal(InstanceHandle instanceHandle, Category category, bool createRegistries, out Dictionary<InstanceHandle, Inspector> registry)
        {
            Inspector inspector = null;

            Dictionary<Type, Dictionary<InstanceHandle, Inspector>> categoryRegistry;
            if (!_registries.TryGetValue(category, out categoryRegistry) && createRegistries)
            {
                categoryRegistry = new Dictionary<Type, Dictionary<InstanceHandle, Inspector>>();
                _registries.Add(category, categoryRegistry);
            }

            if (categoryRegistry == null)
            {
                registry = null;
                return null;
            }

            if (!categoryRegistry.TryGetValue(instanceHandle.Type, out registry))
            {
                if (createRegistries)
                {
                    registry = new Dictionary<InstanceHandle, Inspector>();
                    categoryRegistry.Add(instanceHandle.Type, registry);
                }
                else
                {
                    return inspector;
                }
            }

            registry.TryGetValue(instanceHandle, out inspector);
            return inspector;
        }

        private CategoryButton GetCategoryButton(Category category, bool create = false)
        {
            // Search for it if already created
            if (_categories.TryGetValue(category, out var button) || !create) return button;

            button = CategoryFlex.Append<CategoryButton>(category.Id);
            button.LayoutStyle = Style.Instantiate<LayoutStyle>("CategoryButton");
            button.Category = category;
            button.Callback = () => SelectCategoryButton(button);
            _categories.Add(category, button);

            if (_selectedCategory == null)
            {
                SelectCategoryButton(button);
            }

            return button;
        }

        private Controller ComputeIdealPreviousItem(Item item)
        {
            if (item.Parent is null or SceneRegistry) return null;

            var parent = GetHierarchyItemButton(item.Parent, true);

            Controller idealPreviousItem = null;
            foreach (var child in HierarchyFlex.Children)
            {
                if (child is HierarchyItemButton childButton)
                {
                    if (childButton.Item.Parent == item.Parent || childButton == parent)
                    {
                        idealPreviousItem = childButton;
                    }
                    else
                    {
                        if (idealPreviousItem != null)
                        {
                            break;
                        }
                    }
                }
            }
            return idealPreviousItem;
        }

        private HierarchyItemButton GetHierarchyItemButton(Item item, bool create = false)
        {
            // Search for it if already created
            if (_items.TryGetValue(item, out var button) || !create) return button;

            var idealPreviousItem = ComputeIdealPreviousItem(item);
            button = idealPreviousItem != null ?
                HierarchyFlex.InsertAfter<HierarchyItemButton>(item.Label, idealPreviousItem)
                : HierarchyFlex.Append<HierarchyItemButton>(item.Label);
            button.LayoutStyle = Style.Instantiate<LayoutStyle>("HierarchyItemButton");
            button.Item = item;
            button.LayoutStyle.SetIndent((item.Depth - 1) * 10);
            button.LayoutStyle.SetWidth(button.LayoutStyle.size.x - item.Depth * 10);
            button.Label.Callback = () => SelectHierarchyItemButton(button);
            button.Foldout.Callback = () => ToggleFoldItem(button);
            _items.Add(item, button);
            return button;
        }

        private void TryRemoveHierarchyItemButton(Item item)
        {
            var button = GetHierarchyItemButton(item, false);
            if (button == null) return;

            button.Counter--;
            if (button.Counter != 0) return;

            _items.Remove(item);
            HierarchyFlex.Remove(button, true);
        }

        private void SelectCategoryButton(CategoryButton categoryButton)
        {
            if (_selectedCategory == categoryButton) return;

            SelectHierarchyItemButton(null);

            Flex.ForgetAll();

            if (_selectedCategory != null)
            {
                _selectedCategory.State = false;
            }

            _selectedCategory = categoryButton;

            if (_selectedCategory != null)
            {
                _selectedCategory.State = true;

                SelectCategory(categoryButton.Category);
            }

            _scrollView.Progress = 1.0f;
        }

        private void SelectCategory(Category category)
        {
            if (!_registries.TryGetValue(category, out var categoryRegistry)) return;
            foreach (var typeRegistry in categoryRegistry)
            {
                foreach (var inspector in typeRegistry.Value)
                {
                    Flex.Remember(inspector.Value);

                    if (_debugInterface)
                    {
                        _debugInterface.SetTransparencyRecursive(inspector.Value, !_debugInterface.OpacityOverride);
                    }
                }
            }
        }

        private void ToggleFoldItem(HierarchyItemButton button)
        {
            if (button == null) return;

            if (button.Foldout.State)
            {
                FoldItem(button);
            }
            else
            {
                UnfoldItem(button);
            }
        }

        private void FoldItem(HierarchyItemButton button)
        {
            button.Foldout.State = false;
        }

        private void UnfoldItem(HierarchyItemButton button)
        {
            button.Foldout.State = true;
        }

        private void SelectHierarchyItemButton(HierarchyItemButton button)
        {
            if (_selectedItem == button)
            {
                ToggleFoldItem(button);
                return;
            }

            SelectCategoryButton(null);

            Flex.ForgetAll();

            if (_selectedItem != null)
            {
                _selectedItem.Item?.ClearContent();

                _selectedItem.Label.State = false;
            }

            _selectedItem = button;

            if (_selectedItem != null)
            {
                _selectedItem.Label.State = true;

                SelectItem(_selectedItem.Item);

                UnfoldItem(button);
            }

            _scrollView.Progress = 1.0f;
        }

        private void SelectItem(Item item)
        {
            item.BuildContent();

            SelectCategory(item.Category);
        }

        internal void SetPanelPosition(RuntimeSettings.DistanceOption distanceOption, bool skipAnimation = false)
        {
            var inspectorPanelPositions = ValueContainer<Vector3>.Load("InspectorsPanelPositions");
            _targetPosition = distanceOption switch
            {
                RuntimeSettings.DistanceOption.Close => inspectorPanelPositions["Close"],
                RuntimeSettings.DistanceOption.Far => inspectorPanelPositions["Far"],
                _ => inspectorPanelPositions["Default"]
            };

            if (skipAnimation)
            {
                SphericalCoordinates = _targetPosition;
                _currentPosition = _targetPosition;
                return;
            }

            _lerpCompleted = false;
        }

        private void Update()
        {
            if (_hierarchyIcon.State)
            {
                Hierarchy.Manager.Instance?.Refresh();
            }

            if (_lerpCompleted) return;
            _currentPosition = Utils.LerpPosition(_currentPosition, _targetPosition, _lerpSpeed);
            _lerpCompleted = _currentPosition == _targetPosition;
            SphericalCoordinates = _currentPosition;
        }
    }
}
