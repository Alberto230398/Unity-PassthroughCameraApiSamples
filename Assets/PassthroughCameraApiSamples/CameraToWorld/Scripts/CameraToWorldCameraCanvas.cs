// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using System.Net;
using System.Threading;
using Meta.XR;
using Meta.XR.Samples;
using OVR.OpenVR;
using UnityEngine;
using UnityEngine.UI;

namespace PassthroughCameraSamples.CameraToWorld
{
    [MetaCodeSample("PassthroughCameraApiSamples-CameraToWorld")]
    public class CameraToWorldCameraCanvas : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private Text m_debugText;
        [SerializeField] private RawImage m_image;
        private Texture2D m_cameraSnapshot;

        [Header("Sender")]
        public int port = 8080;
        public int quality = 50;
        public RawImage sourceImage;

        private HttpListener listener;
        private Thread serverThread;
        private byte[] currentFrame;
        private readonly object frameLock = new object();

        public void MakeCameraSnapshot()
        {
            if (!m_cameraAccess.IsPlaying)
            {
                Debug.LogError("!m_cameraAccess.IsPlaying");
                return;
            }

            if (m_cameraSnapshot == null)
            {
                var size = m_cameraAccess.CurrentResolution;
                m_cameraSnapshot = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false);
            }

            var pixels = m_cameraAccess.GetColors();
            m_cameraSnapshot.LoadRawTextureData(pixels);
            m_cameraSnapshot.Apply();

            StopCoroutine(ResumeStreamingFromCameraCor());
            m_image.texture = m_cameraSnapshot;
        }

        public void ResumeStreamingFromCamera()
        {
            StartCoroutine(ResumeStreamingFromCameraCor());
        }

        private IEnumerator ResumeStreamingFromCameraCor()
        {
            while (!m_cameraAccess.IsPlaying)
            {
                yield return null;
            }
            m_image.texture = m_cameraAccess.GetTexture();
        }

        private IEnumerator Start()
        {
            m_debugText.text = "No permission granted.";
            while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
            {
                yield return null;
            }
            m_debugText.text = "Permission granted.";

            while (!m_cameraAccess.IsPlaying)
            {
                yield return null;
            }
            ResumeStreamingFromCamera();

            listener = new HttpListener();
            listener.Prefixes.Add($"http://*:{port}/");
            listener.Start();

            serverThread = new Thread(ServerLoop);
            serverThread.IsBackground = true;
            serverThread.Start();
        }

        void ServerLoop()
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = listener.GetContext();
                    var response = context.Response;

                    response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
                    response.Headers.Add("Cache-Control", "no-cache");

                    var output = response.OutputStream;

                    // Invia frames in loop finché il client è connesso
                    while (true)
                    {
                        byte[] frame;
                        lock (frameLock)
                        {
                            frame = currentFrame;
                        }

                        if (frame == null) { Thread.Sleep(33); continue; }

                        try
                        {
                            string header = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n";
                            byte[] headerBytes = System.Text.Encoding.UTF8.GetBytes(header);

                            output.Write(headerBytes, 0, headerBytes.Length);
                            output.Write(frame, 0, frame.Length);
                            output.Write(System.Text.Encoding.UTF8.GetBytes("\r\n"), 0, 2);
                            output.Flush();

                            Thread.Sleep(33); // ~30fps
                        }
                        catch
                        {
                            // Client disconnesso
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    if (listener.IsListening)
                        Debug.LogError($"Server error: {e.Message}");
                }
            }
        }

        void OnDestroy()
        {
            listener?.Stop();
            serverThread?.Abort();
        }

        private void Update()
        {
            if (m_cameraSnapshot!=null)
            {
                byte[] jpg = m_cameraSnapshot.EncodeToJPG(quality);
                lock (frameLock)
                {
                    currentFrame = jpg;
                }
            }
        }

    }
}
