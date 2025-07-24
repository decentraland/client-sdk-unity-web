using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using LiveKit;
using LiveKit.Proto;
using LiveKit.Rooms;
using LiveKit.Rooms.Participants;
using LiveKit.Rooms.TrackPublications;
using LiveKit.Rooms.Tracks;
using UnityEngine.SceneManagement;

public class ExampleRoom : MonoBehaviour
{
    private Room m_Room;

    public GridLayoutGroup ViewContainer;
    public RawImage ViewPrefab;
    public Button DisconnectButton;
    [SerializeField] private GameObject microphoneObject;

    private void Start()
    {
        StartAsync().Forget();
    }

    private async UniTaskVoid StartAsync()
    {
        // New Room must be called when WebGL assembly is loaded
        m_Room = new Room();

        // Setup the callbacks before connecting to the Room
        m_Room.Participants.UpdatesFromParticipant += (p, update) =>
        {
            if (update == UpdateFromParticipant.Connected)
                Debug.Log($"Participant connected: {p.Sid}");
        };
        m_Room.TrackSubscribed += (track, publication, participant) => HandleAddedTrack(track, publication);

        var c = await m_Room.ConnectAsync(JoinMenu.LivekitURL, JoinMenu.RoomToken, CancellationToken.None, true);

        if (c.success == false)
        {
            Debug.Log($"Failed to connect to the room !: {c.errorMessage}");
            return;
        }

        Debug.Log("Connected to the room");

        DisconnectButton.onClick.AddListener(() =>
        {
            m_Room.DisconnectAsync(CancellationToken.None);
            SceneManager.LoadScene("JoinScene", LoadSceneMode.Single);
        });

        microphoneObject = new GameObject("microphone");
        var audioFilter = microphoneObject.AddComponent<AudioFilter>();

        var microphoneName = Microphone.devices!.First();

        var audioSource = microphoneObject.AddComponent<AudioSource>();
        audioSource.loop = true;
        audioSource.clip = Microphone.Start(microphoneName, true, 1, 48000); //frequency is not guaranteed
        // Wait until mic is initialized
        await UniTask.WaitWhile(() => !(Microphone.GetPosition(microphoneName) > 0)).Timeout(TimeSpan.FromSeconds(5));

        // Play back the captured audio
        audioSource.Play();

        IRtcAudioSource source = new OptimizedMonoRtcAudioSource(audioFilter);
        var myTrack = m_Room.AudioTracks.CreateAudioTrack("own", source);
    }

    private void HandleAddedTrack(ITrack track, TrackPublication publication)
    {
        if (track.Kind == TrackKind.KindAudio)
        {
            throw new NotImplementedException();
        }
    }

    private void OnDestroy()
    {
        if (m_Room != null)
        {
            m_Room.DisconnectAsync(CancellationToken.None);
        }
    }
}