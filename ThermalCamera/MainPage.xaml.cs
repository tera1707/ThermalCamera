using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using System.Diagnostics;

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

        private void Button_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {

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
    }
}
