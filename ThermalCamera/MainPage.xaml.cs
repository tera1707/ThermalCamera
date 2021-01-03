using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using System.Diagnostics;
using System.IO;
using Windows.UI.Xaml;
using Windows.Networking.Sockets;
using System.Collections.Generic;

namespace ThermalCamera
{
    /// <summary>
    /// Sample app that reads data over I2C from an attached ADXL345 accelerometer
    /// MSのサンプルをもとに作成
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private Mlx90640 ThermCam = new Mlx90640();

        public double UpperLimit
        {
            get => ThermCam.UpperLimit;
            set
            {
                if (value > LowerLimit)
                {
                    // サーマル画像の上限温度を設定
                    ThermCam.UpperLimit = value;
                }
            }
        }

        public double LowerLimit
        {
            get => ThermCam.LowerLimit;
            set
            {
                if (value < UpperLimit)
                {
                    // サーマル画像の下限温度を設定
                    ThermCam.LowerLimit = value;
                }
            }
        }

        public MainPage()
        {
            this.InitializeComponent();

            DataContext = this;

            // アプリ終了時のクリーンアップ処理のハンドラを登録
            Unloaded += MainPage_Unloaded;
        }

        private async void Button_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                await InitTcpClient("192.168.31.21", "22110");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        private async void Page_Loaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            await ThermCam.InitThermalCamera();

            var t = Task.Run(async () =>
            {
                while (true)
                {
                    // 手順１ サーマルカメラから温度データを取得(時間がかかるのでUIスレッドでやらないこと)
                    var result = ThermCam.GetTemperatureData();
                    
                    var resultList = result.ToList();

                    // データを送信
                    var sendData = new List<byte>();

                    resultList.ForEach(x =>
                    {
                        BitConverter.GetBytes(x)
                                    .ToList()
                                    .ForEach(y => sendData.Add(y));
                    });

                    var sendByteData = sendData.ToArray();

                    SendCharData(sendByteData);
                    // 送信ここまで


                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                    {
                        // 手順２ サーマルカメラから温度画像を取得(GetWriteableBitmap()を直前に実行している必要あり)
                        var bmp = await ThermCam.GetWriteableBitmap();

                        // 中心3マス分の値をとる
                        var center = result.Where((res, i) =>
                        {
                            int columnCenter = 32 / 2;
                            int rowCenter = 24 / 2;

                            int column = i % 32;
                            int row = i / 32;

                            return ((column >= columnCenter - 1) && (column <= columnCenter + 1))
                                    && ((row >= rowCenter - 1) && (row <= rowCenter + 1));
                        });

                        // 手順３ 採った温度データと画像をUIにセット
                        imageMain.Source = bmp;
                        tbTemparature.Text = center.Average().ToString("F2") + " ℃";
                    });

                    // 手順４ 二週目の前にとりあえずDelay(1)
                    await Task.Delay(1);
                }
            });
        }

        private void MainPage_Unloaded(object sender, object args)
        {
            ThermCam.Dispose();
            ThermCam = null;
        }

        // ---------------------------------------------
#if true
        BinaryReader reader = null;
        BinaryWriter writer = null;

        private async Task InitTcpServer(string ipAddr, string portNo)
        {
            StreamSocketListener listener = new StreamSocketListener();
            listener.Control.KeepAlive = false;
            listener.ConnectionReceived += OnConnection;

            var hostName = new Windows.Networking.HostName(ipAddr);
            await listener.BindEndpointAsync(hostName, portNo);
            //await listener.BindServiceNameAsync("22112");//どのIPでもOKでポートNoだけで指定したければBindServiceNameAsyncを使う
        }

        private async Task InitTcpClient(string ipAddr, string portNo)
        {
            var streamSocket = new Windows.Networking.Sockets.StreamSocket();
            var hostName = new Windows.Networking.HostName(ipAddr);
            await streamSocket.ConnectAsync(hostName, portNo);
            StartReceiving(streamSocket);
        }

        private void StartReceiving(StreamSocket ss)
        {
            writer = new BinaryWriter(ss.OutputStream.AsStreamForWrite());
            reader = new BinaryReader(ss.InputStream.AsStreamForRead());

            // 受信タスク開始
            var t = Task.Run(() =>
            {
                int read = 0;
                var buf = new byte[256];
                var size = buf.Length;

                while (true)
                {
                    try
                    {
                        read = reader.Read(buf, 0, size);
                        var d = BitConverter.ToDouble(buf, 0);
                        Debug.WriteLine("読んだバイト数：" + read + " doubleの数：" + d);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("BinaryReader.Read()例外：" + ex.Message);
                        break;
                    }
                }

                Debug.WriteLine("受信ループ終了");
            });

        }

        private void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            StartReceiving(args.Socket);
        }

        /// <summary>
        /// アプリが終わるときに呼びたいがどこで呼べばいいかわからない
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DisposeStreams(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Page_Unloaded");
            if (reader != null)
            {
                reader.Dispose();
            }
            if (writer != null)
            {
                writer.Dispose();
            }
        }

        private void SendCharData(byte[] data)
        {
            if (writer == null) return;

            writer.Write(data, 0, data.Length);
            writer.Flush();
            //await writer.FlushAsync();
            Debug.WriteLine("送ったデータ：" + BitConverter.ToDouble(data, 0));
        }
#endif
    }
}
