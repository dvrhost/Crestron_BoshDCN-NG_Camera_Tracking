using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using Crestron.SimplSharp;                          				// For Basic SIMPL# Classes
using Crestron.SimplSharp.CrestronSockets;


namespace BoschPTZPresets
{
    public class BoschAutoDomePTZClass
    {
        //IP Chanel Moxa other Ethernet 2 RS232 Devices
        private bool ConnectedToIP;
        private bool debug;
        private uint TCPBufferSize { get; set; }
        private TCPClient TcpClient { get; set; }
        private CTimer reconnectTimer;
        private int Port { get; set; }
        private bool manualDisconnect { get; set; }
        private bool IPChanInitialized;
        private Dictionary<SocketStatus, ushort> sockStatusDict = new Dictionary<SocketStatus, ushort>()
        {
            {SocketStatus.SOCKET_STATUS_NO_CONNECT, 0},
            {SocketStatus.SOCKET_STATUS_WAITING, 1},
            {SocketStatus.SOCKET_STATUS_CONNECTED, 2},
            {SocketStatus.SOCKET_STATUS_CONNECT_FAILED, 3},
            {SocketStatus.SOCKET_STATUS_BROKEN_REMOTELY, 4},
            {SocketStatus.SOCKET_STATUS_BROKEN_LOCALLY, 5},
            {SocketStatus.SOCKET_STATUS_DNS_LOOKUP, 6},
            {SocketStatus.SOCKET_STATUS_DNS_FAILED, 7},
            {SocketStatus.SOCKET_STATUS_DNS_RESOLVED, 8},
            {SocketStatus.SOCKET_STATUS_LINK_LOST,9},
            {SocketStatus.SOCKET_STATUS_SOCKET_NOT_EXIST,10}
        };

        //Parce Data
        readonly byte Opcode = 0x07;    //Auxiliary ON/OFF and Preposition SET/SHOT Commands
        byte AdrressMSB;
        byte AddressLSB;
        const int DataBitsCounts = 7; //Used Bits in bytes of data
        const int FuncCodeBitsLen = 4; //Bits of function code
        public int CameraAddress { get; private set; }
        public int CurrentPresetSelected { get; private set; }

        public delegate void PresetSelected(ushort Preset);
        public PresetSelected PresedCalled { get; set; }

        public delegate void InitializedStatusHandler(ushort status);
        public InitializedStatusHandler InitializedStatus { get; set; }

        public delegate void ConnectionStatusHandler(SimplSharpString serialStatus, ushort analogStatus);
        public ConnectionStatusHandler ConnectionStatus { get; set; }


        private enum ErrorLevel { Notice, Warning, Error, None }

        public BoschAutoDomePTZClass()
        {

        }
        #region Debug

        private void Debug(string msg, ErrorLevel errLevel)
        {
            if (debug)
            {
                CrestronConsole.PrintLine(msg);
                if (errLevel != ErrorLevel.None)
                {
                    switch (errLevel)
                    {
                        case ErrorLevel.Notice:
                            ErrorLog.Notice(msg);
                            break;
                        case ErrorLevel.Warning:
                            ErrorLog.Warn(msg);
                            break;
                        case ErrorLevel.Error:
                            ErrorLog.Error(msg);
                            break;
                    }
                }
            }
        }
        
        // enable logging to ErrorLog
        
        public void EnableDebug()
        {
            debug = true;
            CrestronConsole.PrintLine("Debug Enabled");
        }

        
        // disable logging to ErrorLog
        
        public void DisableDebug()
        {
            debug = false;
            CrestronConsole.PrintLine("Debug Disabled");
        }
        #endregion

        public void Initialize(ushort Address)
        {
            BitArray _LSB7bit = new BitArray(8, false);
            BitArray _MSB7bit = new BitArray(8, false);
            byte[] mBytes = new byte[1];

            CameraAddress = Address - 1;
            mBytes[0] = (byte)(CameraAddress >> 8);
            BitArray MSB = new BitArray(new byte[] { mBytes[0] });
            mBytes[0] = (byte)(CameraAddress);
            BitArray LSB = new BitArray(new byte[] { mBytes[0] });
            for (int i = 0; i < 7; i++)
            {
                _LSB7bit.Set(i, LSB.Get(i));
            }
            _MSB7bit.Set(0, LSB.Get(7));
            for (int i = 0; i < 6; i++)
            {
                _MSB7bit.Set(i + 1, MSB.Get(i));
            }
            AdrressMSB = (byte)BitsToNumeral(_MSB7bit);
            AddressLSB = (byte)BitsToNumeral(_LSB7bit);
        }

        
        public void MessageFromDev(string Message)
        {
            byte[] byteArray = new byte[Message.Length];
            int i = 0;
            foreach (var value in Message)
            {
                try
                {
                    byte number = Convert.ToByte(value);
                    byteArray.SetValue(number, i);
                    i++;
                }
                catch (FormatException)
                {          
                    Debug(string.Format("Bad Format of received data from RS-232: '{0}'", value.ToString()), ErrorLevel.Warning);
                }
            }
            Debug(string.Format("Reseived bytes : {0}", BitConverter.ToString(byteArray)), ErrorLevel.None);
            ParceMessage(byteArray);
            
        }
        private static BitArray BitsReverse(BitArray bits)
        {
            int len = bits.Count;
            BitArray a = new BitArray(bits);
            BitArray b = new BitArray(bits);

            for (int i = 0, j = len - 1; i < len; ++i, --j)
            {
                a[i] = a[i] ^ b[j];
                b[j] = a[i] ^ b[j];
                a[i] = a[i] ^ b[j];
            }

            return a;
        }

        private static BitArray GetBits(byte Byte)
        {
            byte[] mBytes = new byte[1];
            mBytes[0] = Byte;
            BitArray Bits = new BitArray(mBytes);

            return Bits;
        }
        private void ParceMessage(byte[] MSG)
        {

            int MsgLen;
            BitArray _PresetBits = new BitArray(10); //Len is 10 bits
            BitArray _CommandBits = new BitArray(4); //Len is 4 bits
            BitArray MsgLenBytes = new BitArray(new byte[] { MSG[0] });

            if (MsgLenBytes.Get(7) == false)
            {
                Debug("Wrong Input Data From DCN-NG In Input",ErrorLevel.Notice);
                return;
            }
            MsgLenBytes.Set(7, false); //Set Bit7 to false
            MsgLen = BitsToNumeral(MsgLenBytes);//Get Message length

            Debug(string.Format("Message length is: {0}", MsgLen), ErrorLevel.None);


            if ((int)MsgLen <= MSG.Length - 1 && MsgLen == 6)//Received message length correct
            {
                Debug(string.Format("Parse Message, Length is valid: {0}", MsgLen), ErrorLevel.None);
                if (MSG[1] == AdrressMSB && MSG[2] == AddressLSB && MSG[3] == Opcode) //Address and Opcode Match
                {
                    Debug("Opcode in command is Auxiliary ON/OFF and Preposition SET/SHOT Commands", ErrorLevel.None);
                    
                    var DataBits1 = new BitArray(new byte[] { MSG[4] });
                    var DataBits2 = new BitArray(new byte[] { MSG[5] });
                    for (int i = 0; i < FuncCodeBitsLen; i++) //Get  function Code
                    {
                        _CommandBits.Set(i, DataBits1.Get(i));
                    }
                    for (int i = 0; i < DataBitsCounts; i++)
                    {
                        _PresetBits.Set(i, DataBits2.Get(i));
                    }
                    for (int i = FuncCodeBitsLen; i < DataBitsCounts; i++)
                    {
                        _PresetBits.Set(i + 3, DataBits1.Get(i));
                    }
                    if (BitsToNumeral(_CommandBits) == 5)//Call Preset Command
                    {
                        CurrentPresetSelected = BitsToNumeral(_PresetBits);
                        Debug(string.Format("Preset Number is {0}", CurrentPresetSelected), ErrorLevel.None);
                        if (PresedCalled != null)
                            PresedCalled(Convert.ToUInt16(CurrentPresetSelected));
                    }
                }
                else
                    Debug("Wrong Address or Command Code", ErrorLevel.None);
            }
            else
                Debug("Wrong Input Data Length or truncated data", ErrorLevel.None);
        }

        private int BitsToNumeral(BitArray bitArray)
        {
            if (bitArray == null)
                throw new ArgumentNullException("Null Exeption in Module");
            if (bitArray.Length > 32)
                throw new ArgumentException("Length must be at 32 bits.");
            int[] result = new int[1];
            bitArray.CopyTo(result, 0);
            return result[0];

        }
        
        #region TCPIP Channel

        public void IPChanelInitialize(string IP, uint Port, ushort BufferSize, ushort Address)
        {
            if (!IPChanInitialized)
            {
                if (BufferSize > 0)
                    TCPBufferSize = BufferSize;
                else
                    TCPBufferSize = 1024;

                if (IP.Length > 0 && Port > 0)
                {
                    TcpClient = new TCPClient(IP, (int)Port, (int)TCPBufferSize);
                    TcpClient.SocketStatusChange += new TCPClientSocketStatusChangeEventHandler(ClientSocketStatusChange);
                }

                if (TcpClient.PortNumber > 0 && TcpClient.AddressClientConnectedTo != string.Empty)
                {
                    Initialize(Address);//Init Cam ID Address
                    IPChanInitialized = true;
                    if (InitializedStatus != null) //Notify SIMPL+ Module
                        InitializedStatus(Convert.ToUInt16(IPChanInitialized));
                    Debug(string.Format("TCPClient initialized: IP: {0}, Port: {1}",
                                TcpClient.AddressClientConnectedTo, TcpClient.PortNumber), ErrorLevel.Notice);
                }
                else
                {
                    IPChanInitialized = false;
                    Debug("TCPClient can't initialized, missing data", ErrorLevel.Notice);
                }
            }
        }
        public void Connect()
        {
            SocketErrorCodes err = new SocketErrorCodes();
            if (ConnectedToIP == false && IPChanInitialized == true)
            {
                try
                {
                    manualDisconnect = false;
                    err = TcpClient.ConnectToServerAsync(ClientConnectCallBackFunction);
                    TcpClient.ReceiveDataAsync(SerialRecieveCallBack);
                    Debug(string.Format("Connection attempt: {0}, with status: {1}", TcpClient.AddressClientConnectedTo, err.ToString()), ErrorLevel.Notice);
                }
                catch (Exception e)
                {
                    Debug(string.Format("Exeption on connect with error: {0}", e.Message), ErrorLevel.Error);
                }
            }
            else
            {
                Debug("Exeption on connect: Connecting befor TCPClient initialized.", ErrorLevel.Notice);
            }
        }
        public void Disconnect()
        {
            SocketErrorCodes err = new SocketErrorCodes();
            try
            {
                manualDisconnect = true;
                ConnectedToIP = false;
                IPChanInitialized = false;
                if (InitializedStatus != null) //Notify SIMPL+ Module
                    InitializedStatus(Convert.ToUInt16(IPChanInitialized));
                err = TcpClient.DisconnectFromServer();
                Debug(string.Format("Disconnect attempt: {0}, with error: {1}", TcpClient.AddressClientConnectedTo, err.ToString()), ErrorLevel.Notice);
            }
            catch (Exception e)
            {
                Debug(string.Format("Exeption on connect with error: {0}", e.Message), ErrorLevel.Error);
            }
        }
        private void SendData(byte[] Message)
        {
            if (Message.Length > 0 && ConnectedToIP)
            {
                SocketErrorCodes err = new SocketErrorCodes();
                err = TcpClient.SendData(Message, Message.Length);
                Debug(string.Format("Byte data transmitted: {0}, with code: {1}", Encoding.ASCII.GetString(Message, 0, Message.Length), err), ErrorLevel.None);

            }
        }
        private void TryReconnect()
        {
            if (!manualDisconnect)
            {
                Debug("Attempting to reconnect...", ErrorLevel.None);
                reconnectTimer = new CTimer(o => { TcpClient.ConnectToServerAsync(ClientConnectCallBackFunction); }, 10000);
            }
        }
        private void ClientSocketStatusChange(TCPClient mytcpclient, SocketStatus clientsocketstatus)
        {
            // Check to see if it just connected or disconnected
            if (ConnectionStatus != null) //Notify  if subscribe
            {
                if (sockStatusDict.ContainsKey(clientsocketstatus))
                    ConnectionStatus(clientsocketstatus.ToString(), sockStatusDict[clientsocketstatus]);
            }
            if (clientsocketstatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                TcpClient.ReceiveDataAsync(SerialRecieveCallBack);
            }
            else
            {
                TryReconnect();
            }
        }
        private void SerialRecieveCallBack(TCPClient myTcpClient, int numberOfBytesReceived)
        {
            byte[] rxBuffer;

            if (numberOfBytesReceived > 0)
            {
                rxBuffer = myTcpClient.IncomingDataBuffer;

                Debug(string.Format("TCP RAW Data: {0}", Encoding.ASCII.GetString(rxBuffer, 0, rxBuffer.Length)), ErrorLevel.None);
                Debug(string.Format("Reseived bytes from TCP : {0}", BitConverter.ToString(rxBuffer)), ErrorLevel.None);
                ParceMessage(rxBuffer);
            }
            TcpClient.ReceiveDataAsync(SerialRecieveCallBack);
        }
        private void ClientConnectCallBackFunction(TCPClient TcpClient)
        {
            if (TcpClient.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                ConnectedToIP = true;
            else
            {
                ConnectedToIP = false;
                TryReconnect();
            }
        }
        #endregion
    }
}
