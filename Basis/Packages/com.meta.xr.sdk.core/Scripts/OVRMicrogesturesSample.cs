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

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Sample script which reads hand microgesture event data and displays recognized gestures on a UI panel.
/// </summary>
public class OVRMicrogesturesSample : MonoBehaviour
{
    [SerializeField]
    private OVRMicrogestureEventSource leftGestureSource;

    [SerializeField]
    private OVRMicrogestureEventSource rightGestureSource;

    [Header("Gesture Labels")]
    [SerializeField]
    private Text leftGestureLabel;

    [SerializeField]
    private Text rightGestureLabel;

    [SerializeField]
    private float gestureShowDuration = 1.5f;

    [Header("Navigation Icons Left")]
    [SerializeField]
    private Image leftArrowL;

    [SerializeField]
    private Image rightArrowL;

    [SerializeField]
    private Image upArrowL;

    [SerializeField]
    private Image downArrowL;

    [SerializeField]
    private Image selectIconL;

    [Header("Navigation Icons Right")]
    [SerializeField]
    private Image leftArrowR;

    [SerializeField]
    private Image rightArrowR;

    [SerializeField]
    private Image upArrowR;

    [SerializeField]
    private Image downArrowR;

    [SerializeField]
    private Image selectIconR;

    [Header("Colors")]
    [SerializeField]
    private Color initialColor = Color.white;

    [SerializeField]
    private Color highlightColor = Color.blue;

    [SerializeField]
    private float highlightDuration = 1f;

    private Dictionary<GameObject, Coroutine> highlightCoroutines = new Dictionary<GameObject, Coroutine>();

    void Start()
    {
        leftGestureSource.GestureRecognizedEvent.AddListener(gesture => OnGestureRecognized(OVRPlugin.Hand.HandLeft, gesture));
        rightGestureSource.GestureRecognizedEvent.AddListener(gesture => OnGestureRecognized(OVRPlugin.Hand.HandRight, gesture));
    }

    private void HighlightGesture(OVRPlugin.Hand hand, OVRHand.MicrogestureType gesture)
    {
        switch (gesture)
        {
            case OVRHand.MicrogestureType.SwipeLeft:
                HighlightIcon(hand == OVRPlugin.Hand.HandLeft ? leftArrowL : leftArrowR);
                break;
            case OVRHand.MicrogestureType.SwipeRight:
                HighlightIcon(hand == OVRPlugin.Hand.HandLeft ? rightArrowL : rightArrowR);
                break;
            case OVRHand.MicrogestureType.SwipeForward:
                HighlightIcon(hand == OVRPlugin.Hand.HandLeft ? upArrowL : upArrowR);
                break;
            case OVRHand.MicrogestureType.SwipeBackward:
                HighlightIcon(hand == OVRPlugin.Hand.HandLeft ? downArrowL : downArrowR);
                break;
            case OVRHand.MicrogestureType.ThumbTap:
                HighlightIcon((hand == OVRPlugin.Hand.HandLeft) ? selectIconL : selectIconR);
                break;
        }
    }

    private void HighlightIcon(Image icon)
    {
        if (highlightCoroutines.TryGetValue(icon.gameObject, out Coroutine highlightNavCoroutine))
        {
            StopCoroutine(highlightNavCoroutine);
            highlightCoroutines.Remove(icon.gameObject);
        }
        highlightCoroutines.Add(icon.gameObject, StartCoroutine(HighlightIconCoroutine(icon)));
    }

    private IEnumerator HighlightIconCoroutine(Image navIcon)
    {
        Color initialCol = initialColor;
        navIcon.color = highlightColor;
        float timer = 0;
        while (timer < highlightDuration)
        {
            navIcon.color = Color.Lerp(highlightColor, initialCol, (timer / highlightDuration));
            timer += Time.deltaTime;
            yield return null;
        }
    }

    private void HighlightIcon(Image navIcon, bool state)
    {
        navIcon.color = state ? highlightColor : initialColor;
    }

    private void OnGestureRecognized(OVRPlugin.Hand hand, OVRHand.MicrogestureType gesture)
    {
        HighlightGesture(hand, gesture);
        ShowRecognizedGestureLabel((hand == OVRPlugin.Hand.HandLeft) ? leftGestureLabel : rightGestureLabel, gesture.ToString());
    }

    private void ShowRecognizedGestureLabel(Text gestureLabel, string label)
    {
        if (highlightCoroutines.TryGetValue(gestureLabel.gameObject, out Coroutine showGestureLabelCoroutine))
        {
            StopCoroutine(showGestureLabelCoroutine);
            highlightCoroutines.Remove(gestureLabel.gameObject);
        }
        highlightCoroutines.Add(gestureLabel.gameObject, StartCoroutine(ShowGestureLabel(gestureLabel, label)));
    }

    private IEnumerator ShowGestureLabel(Text gestureLabel, string label)
    {
        gestureLabel.text = label;
        yield return new WaitForSeconds(gestureShowDuration);
        gestureLabel.text = string.Empty;
    }
}
