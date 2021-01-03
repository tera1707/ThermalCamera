using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using System.Diagnostics;
using System.IO;
using Windows.UI.Xaml;
using Windows.Networking.Sockets;
using System.Collections.Generic;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;

namespace ThermalCamera
{
    /// <summary>
    /// Sample app that reads data over I2C from an attached ADXL345 accelerometer
    /// MSのサンプルをもとに作成
    /// </summary>
    public sealed partial class MainPage : Page
    {
        // 円周率
        private double M_PI = 3.1415;
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
#if THERMAL_HONTAI
            await InitTcpClient("192.168.31.21", "22110");
#else
            await InitTcpServer("192.168.31.21", "22110");
#endif

        }

        private async void Page_Loaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
#if THERMAL_HONTAI
                // サーマル本体(ラズパイWinIoT側)
                await ThermCam.InitThermalCamera();
                var t = Task.Run(ThermalProcessMain);

                await InitTcpClient("192.168.31.21", "22110");
#else
                // モニター用PC側(Windows10 PC)
                await InitTcpServer("192.168.31.21", "22110");
#endif
                DataRecieved = DisplayDoubleData;

            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        private async Task DisplayDoubleData(double[] result)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                if (result.Length <= 0) return;

                // 手順２ サーマルカメラから温度画像を取得(GetTemperatureData()を直前に実行している必要あり)
                var bmp = await DoubleToRaindowColor(result);

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

        }

        private async void ThermalProcessMain()
        {
            while (true)
            {
                // 手順１ サーマルカメラから温度データを取得(時間がかかるのでUIスレッドでやらないこと)
                var result = ThermCam.GetTemperatureData();

                // tcpでサーマルデータ信
                ConvDoubleToByteAndSendData(result);

                await DisplayDoubleData(result);
                // 手順４ 二週目の前にとりあえずDelay(1)
                await Task.Delay(5);
            }
        }

        private (byte, byte, byte, byte) ColorScaleBCGYR(double in_value)
        {
            // 0.0～1.0 の範囲の値をサーモグラフィみたいな色にする
            // 0.0                    1.0
            // 青    水    緑    黄    赤
            // 最小値以下 = 青
            // 最大値以上 = 赤
            int ret;
            int a = 255;    // alpha値
            int r, g, b;    // RGB値
            double value = in_value;
            double tmp_val = Math.Cos(4 * M_PI * value);
            int col_val = (int)((-tmp_val / 2 + 0.5) * 255);
            if (value >= (4.0 / 4.0)) { r = 255; g = 0; b = 0; }   // 赤
            else if (value >= (3.0 / 4.0)) { r = 255; g = col_val; b = 0; }   // 黄～赤
            else if (value >= (2.0 / 4.0)) { r = col_val; g = 255; b = 0; }   // 緑～黄
            else if (value >= (1.0 / 4.0)) { r = 0; g = 255; b = col_val; }   // 水～緑
            else if (value >= (0.0 / 4.0)) { r = 0; g = col_val; b = 255; }   // 青～水
            else { r = 0; g = 0; b = 255; }   // 青
            ret = (a & 0x000000FF) << 24
                | (r & 0x000000FF) << 16
                | (g & 0x000000FF) << 8
                | (b & 0x000000FF);
            return ((byte)a, (byte)r, (byte)g, (byte)b);
        }

        /// <summary>
        /// 虹色を0.0～1.0で表現したいので、
        /// 指定した上限と下限の温度値の範囲で、0.0～1.0の値をとるよう変換
        /// </summary>
        /// <param name="temperature"></param>
        /// <returns></returns>
        private double TemperatureTo0to1Double(double temperature)
        {
            // 15.0℃～35℃で、虹色を作るものとする。(あとではんい広げたい)
            double ret = 0;// 温度の値を、15度～35度の間で0.0～1.0に直したもの
            double lowlimit = LowerLimit;
            double highlimit = UpperLimit;
            double dif = highlimit - lowlimit;

            if (temperature < lowlimit)
            {
                ret = 0;
            }
            else if (temperature > highlimit)
            {
                ret = 1;
            }
            else
            {
                ret = (temperature - lowlimit) / (highlimit - lowlimit);
            }

            return ret;
        }

        /// <summary>
        /// 温度の値を、32*24のピクセルの描画データに変換する
        /// </summary>
        /// <param name="totalFrameData"></param>
        /// <returns></returns>
        private async Task<BitmapImage> DoubleToRaindowColor(double[] totalFrameData)//temp:温度の値
        {
            int width = 32;
            int height = 24;
            byte[] data = new byte[width * height * 4];

            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < height; j++)
                {
                    // 指定の温度下限～上限の値を、0.0～1.0の値に変換する
                    var v = TemperatureTo0to1Double(totalFrameData[i + j * width]);

                    // 0.0～1.0の値を、虹色を表すバイト列に変換する
                    var c = ColorScaleBCGYR(v);

                    data[4 * (i + j * width)] = c.Item4;            // Blue
                    data[4 * (i + j * width) + 1] = c.Item3;        // Green
                    data[4 * (i + j * width) + 2] = c.Item2;        // Red
                    data[4 * (i + j * width) + 3] = c.Item1;        // alpha
                }
            }

            // サーマル画像を作成
            WriteableBitmap bitmap = new WriteableBitmap(width, height);
            InMemoryRandomAccessStream inMRAS = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, inMRAS);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96.0, 96.0, data);
            await encoder.FlushAsync();
            BitmapImage bitmapImage = new BitmapImage();
            bitmapImage.SetSource(inMRAS);

            return bitmapImage;
        }

        private void MainPage_Unloaded(object sender, object args)
        {
            ThermCam.Dispose();
            ThermCam = null;
        }

        // ---------------------------------------------
#if true
        StreamSocket ss = null;
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
            //var streamSocket = new Windows.Networking.Sockets.StreamSocket();
            ss = new Windows.Networking.Sockets.StreamSocket();
            var hostName = new Windows.Networking.HostName(ipAddr);
            await ss.ConnectAsync(hostName, portNo);
            StartReceiving(ss);
        }

        // tcpでデータを受信したときのハンドラ
        private Func<double[], Task> DataRecieved;

        private void StartReceiving(StreamSocket ss)
        {
            writer = new BinaryWriter(ss.OutputStream.AsStreamForWrite());
            reader = new BinaryReader(ss.InputStream.AsStreamForRead());

            // 受信タスク開始
            var t = Task.Run(() =>
            {
                int read = 0;
                var buf = new byte[65535];
                var size = buf.Length;

                while (true)
                {
                    try
                    {
                        read = reader.Read(buf, 0, 2);

                        var len = BitConverter.ToUInt16(buf, 0);
                        Debug.WriteLine("読んだバイト数：" + len);

                        if (len > 0)
                        {
                            read = reader.Read(buf, 0, len);

                            // 受信したデータ(double配列をbyte配列にしたもの)をdoubleの配列に戻す
                            var dArray = ConvByteArrayToDoubleArray(buf, read);

                            // 受信時の処理
                            DataRecieved?.Invoke(dArray);
                        }
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

        // サーマルデータのdouble配列をbyte配列に変換
        // Client側用処理
        private void ConvDoubleToByteAndSendData(double[] data)
        {
            var resultList = data.ToList();

            // データを送信
            var sendData = new List<byte>();

            resultList.ForEach(x =>
            {
                BitConverter.GetBytes(x)
                            .ToList()
                            .ForEach(y => sendData.Add(y));
            });

            // データ長をListの先頭に挿入
            var len = BitConverter.GetBytes((ushort)sendData.Count());
            sendData.Insert(0, len[0]);
            sendData.Insert(1, len[1]);

            // データをbyte配列にする
            var sendByteData = sendData.ToArray();

            // 送信実施
            SendCharData(sendByteData);
        }

        // 受信したbyte配列をサーマルデータのdouble配列に戻す
        // Server側用処理
        private double[] ConvByteArrayToDoubleArray(byte[] data, int length)
        {
            var sendData = new List<double>();

            var dataList = Enumerable.Range(0, length / sizeof(double))
                                     .Select(x => BitConverter.ToDouble(data, x * sizeof(double)))
                                     .ToList();

            return dataList.ToArray();
        }

        private void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            StartReceiving(args.Socket);
        }

        private void SendCharData(byte[] data)
        {
            if (writer == null) return;

            try
            {
                writer.Write(data, 0, data.Length);
                writer.Flush();
            }
            catch (IOException ioe)
            {
                Debug.WriteLine(ioe.Message);
                CloseConnect();
            }
        }

        private void CloseConnect()
        {
            if (ss != null)
            {
                ss.Dispose();
                ss = null;
            }
            if (writer != null)
            {
                writer.Dispose();
                writer = null;
            }
            if (reader != null)
            {
                reader.Dispose();
                reader = null;
            }
        }
#endif
    }
}
