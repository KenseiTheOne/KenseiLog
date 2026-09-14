using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KenseiLog.Editor {
    internal sealed class TabRenamePopup : EditorWindow {
        private string _initial;
        private Action<string> _onConfirm;

        public static void Show(EditorWindow owner, string current, Action<string> onConfirm) {
            TabRenamePopup popup = CreateInstance<TabRenamePopup>();
            popup.titleContent = new GUIContent("Rename tab");
            popup._initial = current;
            popup._onConfirm = onConfirm;
            popup.position = new Rect(owner.position.center.x - 130f, owner.position.center.y - 32f, 260f, 64f);
            popup.ShowUtility();
        }

        private void CreateGUI() {
            TextField field = new TextField { value = _initial };
            field.style.marginTop = 6f;
            field.style.marginLeft = 6f;
            field.style.marginRight = 6f;
            field.RegisterCallback<KeyDownEvent>(evt => {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) {
                    Confirm(field.value);
                } else if (evt.keyCode == KeyCode.Escape) {
                    Close();
                }
            });
            rootVisualElement.Add(field);

            Button confirm = new Button(() => Confirm(field.value)) { text = "Rename" };
            confirm.style.marginLeft = 6f;
            confirm.style.marginRight = 6f;
            rootVisualElement.Add(confirm);

            field.schedule.Execute(() => {
                field.Focus();
                field.SelectAll();
            });
        }

        private void Confirm(string value) {
            if (!string.IsNullOrWhiteSpace(value)) {
                _onConfirm(value.Trim());
            }
            Close();
        }
    }
}
