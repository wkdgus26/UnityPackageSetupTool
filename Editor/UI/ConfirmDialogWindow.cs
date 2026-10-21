using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PackageSetupTool
{
    // EditorUtility.DisplayDialog 를 대신하는 확인 창. OS 대화상자는 위치를 정할 수 없어 Unity 메인 창 가운데에 뜨므로
    // 도구 창 가운데에 띄우려고 직접 만든다(DialogPlacement). 창 높이는 문장 길이에 맞추고 크기는 고정한다.
    // 버튼은 확인 · 대체 · 취소 순서이고, 대체·취소 문구가 없으면 그 버튼을 두지 않는다(확인만 있으면 알림 창).
    // 창의 닫기(X)와 Esc 는 취소다. 처음에 확인 버튼에 포커스가 있어 Enter 로 누를 수 있다.
    public class ConfirmDialogWindow : EditorWindow
    {
        private const string UXML_FILE_NAME = "ConfirmDialogWindow.uxml";
        private const float WIDTH = 400f;
        // 내용 높이를 재기 전의 높이. 첫 레이아웃에서 내용 높이로 바뀐다.
        private const float INITIAL_HEIGHT = 120f;

        private VisualElement _content;
        private Button _confirmButton;
        private ConfirmDialogChoice _choice = ConfirmDialogChoice.Cancel;

        public void Initialize(string title, string message, string confirmText, string cancelText = null, string alternativeText = null)
        {
            titleContent = new GUIContent(title);

            _content = ResourceLoader.GetUXML(UXML_FILE_NAME);
            // 창이 내용보다 낮아도 줄어들지 않게 해 내용 높이를 그대로 잰다.
            _content.style.flexShrink = 0;
            rootVisualElement.Add(_content);

            var messageLabel = _content.Q<Label>("MessageLabel");
            messageLabel.text = message;
            // 실패 사유 원문을 복사할 수 있게 한다.
            messageLabel.selection.isSelectable = true;

            _confirmButton = SetupButton("ConfirmButton", confirmText, ConfirmDialogChoice.Confirm);
            SetupButton("AlternativeButton", alternativeText, ConfirmDialogChoice.Alternative);
            SetupButton("CancelButton", cancelText, ConfirmDialogChoice.Cancel);

            rootVisualElement.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _content.RegisterCallback<GeometryChangedEvent>(OnContentGeometryChanged);
        }

        // 도구 창 가운데에 모달로 띄우고 닫힐 때까지 기다린다.
        public ConfirmDialogChoice ShowModalAndWait()
        {
            position = DialogPlacement.GetCenteredRect(new Vector2(WIDTH, INITIAL_HEIGHT));
            ShowModal();
            return _choice;
        }

        // text 가 null 이면 버튼을 숨긴다.
        private Button SetupButton(string buttonName, string text, ConfirmDialogChoice choice)
        {
            var button = _content.Q<Button>(buttonName);
            if (text == null)
            {
                button.style.display = DisplayStyle.None;
                return button;
            }
            button.text = text;
            button.clicked += () =>
            {
                _choice = choice;
                Close();
            };
            return button;
        }

        // 내용 높이가 정해지면 창을 그 높이로 고정하고 도구 창 가운데에 다시 놓는다.
        // ShowModal 이 마지막으로 닫힌 위치·크기(EditorPrefs)를 불러와 덮어쓰므로(6000.6 실측) 높이가 같아도 위치를 맞춘다.
        private void OnContentGeometryChanged(GeometryChangedEvent evt)
        {
            var size = new Vector2(WIDTH, evt.newRect.height);
            Rect target = DialogPlacement.GetCenteredRect(size);
            if (position == target)
            {
                return;
            }
            minSize = size;
            maxSize = size;
            position = target;
            _confirmButton.Focus();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape)
            {
                return;
            }
            evt.StopPropagation();
            Close();
        }
    }
}
