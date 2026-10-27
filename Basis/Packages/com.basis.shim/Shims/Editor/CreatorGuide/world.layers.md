Basis reserves several Unity layers. Use them as described so players, pointers and cameras treat your objects correctly.

| # | Layer | Used for |
| --- | --- | --- |
| 0 | Default | Ordinary world geometry. Players collide with it and pointers hit it. |
| 1 | TransparentFX | Unity default. |
| 2 | Ignore Raycast | Not hit by pointers or raycasts, and ignored by the third person camera. |
| 3 | Player | The local player's character controller. |
| 4 | Water | Unity default. |
| 5 | UI | World space UI. |
| 6 | LocalPlayerAvatar | Your own avatar. Hidden from your first person view where needed. |
| 7 | RemotePlayerAvatar | Other players' avatars. |
| 8 | Interactable | Objects the pointer should highlight and interact with. |
| 9 | OverlayUI | UI drawn over everything, such as the menu. |
| 10 | IgnoredByInteractable | Visible and solid, but pointers pass through. Use for glass or trigger volumes in front of buttons. |
| 11 | HandHeldCameraUI | The hand held camera's own UI. |

## Choosing a layer

- **Walls, floors, props:** Default.
- **Trigger volumes and invisible blockers in front of buttons:** IgnoredByInteractable, so pointers can reach what is behind them.
- **Decorative colliders the pointer should never hit:** Ignore Raycast.
- **Never put your objects on** Player, LocalPlayerAvatar, RemotePlayerAvatar or OverlayUI.

> [!info]
> The layer names and numbers come from the Basis project's Tag Manager. Content is built with them, so do not rename or reorder Basis layers in your project.
