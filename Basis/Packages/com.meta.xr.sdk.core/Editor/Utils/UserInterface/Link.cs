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

using System;
using Meta.XR.Editor.Id;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Meta.XR.Editor.UserInterface
{
    internal class Documentation
    {
        public string Title;
        public string Url;
        public string Id;

        private UrlLinkDescription _link;

        public UrlLinkDescription Link => _link ??= new UrlLinkDescription()
        {
            Content = new GUIContent(Title),
            Id = Id,
            Style = Styles.GUIStyles.DocumentationLinkStyle,
            URL = Url,
            Origin = Origins.Unknown
        };
    }

    internal abstract class LinkDescription : IIdentified
    {
        public string Id { get; set; }

        public GUIStyle Style = Styles.GUIStyles.LinkLabelStyle;
        public GUIContent Content;
        public Color BackgroundColor = UnityEngine.Color.white;
        public Color Color = Color.white;
        public string Label => !string.IsNullOrEmpty(Content.text) ? Content.text : Content.image.name;
        public Origins Origin;
        public IIdentified OriginData;
        public int Order = 10;
        public bool Underline;

        public void Draw(params GUILayoutOption[] options)
        {
            if (!Valid) return;

            if (DrawInternal(options))
            {
                Click();
            }
        }

        public void Click()
        {
            if (!Valid) return;

            OnClicked();
            SendTelemetry();
        }

        public virtual bool Valid => Content.image != null || !string.IsNullOrEmpty(Content.text);

        protected abstract void OnClicked();

        protected virtual bool DrawInternal(params GUILayoutOption[] options)
        {
            using var allColor = new Utils.ColorScope(Utils.ColorScope.Scope.Background, BackgroundColor);
            using var contentColor = new Utils.ColorScope(Utils.ColorScope.Scope.Content, Color);

            var position = GUILayoutUtility.GetRect(Content, Style, options);

            if (Underline)
            {
                Handles.color = Style.normal.textColor;
                Handles.DrawLine(new Vector3(position.xMin + (float)Style.padding.left, position.yMax), new Vector3(position.xMax - (float)Style.padding.right, position.yMax));
                Handles.color = Color.white;
            }

            EditorGUIUtility.AddCursorRect(position, MouseCursor.Link);
            return GUI.Button(position, Content, Style);
        }

        private void SendTelemetry()
        {
            var marker = OVRTelemetry.Start(Telemetry.MarkerId.LinkClick);
            marker = AddAnnotations(marker);
            marker.Send();
        }

        protected virtual OVRTelemetryMarker AddAnnotations(OVRTelemetryMarker marker)
        {
            var newMarker = marker.AddAnnotation(Telemetry.AnnotationType.Label, Label)
                .AddAnnotation(Telemetry.AnnotationType.Action, Id ?? Label)
                .AddAnnotation(Telemetry.AnnotationType.ActionType, GetType().Name)
                .AddAnnotation(Telemetry.AnnotationType.Origin, Origin.ToString())
                .AddAnnotation(Telemetry.AnnotationType.OriginData, OriginData?.Id);

            return newMarker;
        }
    }

    internal class UrlLinkDescription : LinkDescription
    {
        public string URL;

        public override bool Valid => base.Valid && URL != null;

        protected override void OnClicked()
        {
            Application.OpenURL(URL);
        }

        protected override OVRTelemetryMarker AddAnnotations(OVRTelemetryMarker marker)
        {
            return base.AddAnnotations(marker)
                .AddAnnotation(Telemetry.AnnotationType.ActionData, URL);
        }
    }

    internal class AssetLinkDescription : LinkDescription
    {
        public Object Asset;

        public override bool Valid => base.Valid && Asset != null;

        protected override void OnClicked()
        {
            EditorGUIUtility.PingObject(Asset);
            Selection.activeObject = Asset;
        }

        protected override OVRTelemetryMarker AddAnnotations(OVRTelemetryMarker marker)
        {
            return base.AddAnnotations(marker)
                .AddAnnotation(Telemetry.AnnotationType.ActionData, (Asset as IIdentified)?.Id ?? Asset.name);
        }
    }

    internal class ActionLinkDescription : LinkDescription
    {
        public Action Action;
        public IIdentified ActionData;

        public override bool Valid => base.Valid && Action != null;

        protected override void OnClicked()
        {
            Action?.Invoke();
        }

        protected override OVRTelemetryMarker AddAnnotations(OVRTelemetryMarker marker)
        {
            return base.AddAnnotations(marker)
                .AddAnnotation(Telemetry.AnnotationType.ActionData, ActionData?.Id);
        }
    }
}
