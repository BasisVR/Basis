using UnityEngine;

namespace Basis.BasisUI
{
    [RequireComponent(typeof(PanelButton))]
    public class PanelGridItem : PanelComponent
    {

        public struct Data
        {
            public string Title;
            public Texture2D Texture;

            public Data(string title, Texture2D texture)
            {
                Title = title;
                Texture = texture;
            }
        }

        [field:SerializeField] public int DataIndex { get; protected set; }

        public PanelButton Button => _button ??= GetComponent<PanelButton>();
        private PanelButton _button;

        protected PanelGridList _list;


        protected override void Awake()
        {
            base.Awake();
            Button.OnClicked += OnComponentUsed;
        }

        /// <summary>
        /// Modify the item to fit the bound data.
        /// </summary>
        public virtual void BindDataIndex(PanelGridList list, int index)
        {
            _list = list;
            DataIndex = index;
            RefreshVisuals();
        }

        public override void OnComponentUsed()
        {
            base.OnComponentUsed();
            _list.OnItemSelected(DataIndex);
        }

        public void RefreshVisuals()
        {
            Data data = _list.ListData[DataIndex];
            Descriptor.SetTitle(data.Title);
            Descriptor.SetTexture(data.Texture);
            Button.ButtonStyling.ShowIndicator(_list.Value == DataIndex);
        }
    }
}
