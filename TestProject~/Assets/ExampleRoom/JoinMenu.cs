using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class JoinMenu : MonoBehaviour
{
    public static string LivekitURL { get; private set; }
    public static string RoomToken { get; private set; }

    public RawImage PreviewCamera;
    public InputField URLField;
    public InputField TokenField;
    public Button ConnectButton;

    void Start()
    {
        StartCoroutine(StartPreviewCamera());

        ConnectButton.onClick.AddListener(() =>
        {
            LivekitURL = URLField.text;
            RoomToken = TokenField.text;

            if (string.IsNullOrWhiteSpace(RoomToken))
                return;

            SceneManager.LoadScene("RoomScene", LoadSceneMode.Single);
        });
    }

    private IEnumerator StartPreviewCamera()
    {
        Debug.LogError("Preview camera is not supported");
        yield break;
    }
}
