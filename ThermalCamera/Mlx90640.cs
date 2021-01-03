using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.I2c;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace ThermalCamera
{
    class Mlx90640 :IDisposable
    {
        // サーマルカメラMLX90640のアドレス
        private const byte ThermalCameraI2CAddress = 0x33;

        // ラズパイのI2Cデバイスの名前
        private const string I2cDeviceName = "I2C1";

        // EEPROMから読み出すデータ個数(byteで832個)
        private const int EepromDaataLength = 832;

        // サーマルデータの個数(ushortが832個)
        private const int FrameDataLength = 832;//ushortのデータが832個 + コントロールレジスタとステータスレジスタで834個(byte配列にしたら、倍の1638個)

        // I2C制御用クラスインスタンス
        private I2cDevice I2CThermalCamera;

        // EEPROMから読み出したサーマルカメラの設定データ保存用クラス
        private ParamsMLX90640 CamParameters = new ParamsMLX90640();

        // ステータスレジスタ保存
        private ushort StatusRegister = 0;

        // コントロールレジスタ保存
        private ushort ControlRegister = 0;

        // ページ1と2を合わせたフレームのデータ
        private double[] TotalFrameData = new double[FrameDataLength];


        // サーマル画像 上限温度(これより温度が高いと「赤」になる)
        public double UpperLimit = 33;

        // サーマル画像 下限温度(これより温度が低いと「青」になる)
        public double LowerLimit = 20;

        public void Dispose()
        {
            I2CThermalCamera.Dispose();
            I2CThermalCamera = null;
        }

        #region メイン処理

        /// <summary>
        /// サーマルカメラ初期化処理
        /// - DeviceInformationを検索・取得
        /// - I2cConnectionSettingsを作成
        /// - I2cDeviceインスタンスを作成
        /// - サーマルカメラのお決まり手順実施
        ///  - コントロールレジスタ読み出し
        ///  - EEPROM読み込み
        /// </summary>
        /// <returns></returns>
        public async Task InitThermalCamera()
        {
            // すべてのI2Cデバイスを取得するためのセレクタ文字列を取得
            string aqs = I2cDevice.GetDeviceSelector(I2cDeviceName);
            DeviceInformationCollection dis = null;

            try
            {
                // セレクタ文字列を使ってI2Cコントローラデバイスを取得
                dis = await DeviceInformation.FindAllAsync(aqs);

                if (dis.Count == 0)
                {
                    Debug.WriteLine("I2Cコントローラデバイスが見つかりませんでした");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                throw;
            }

            // I2Cアドレスを指定して、デフォルトのI2C設定を作成する
            var settings = new I2cConnectionSettings(ThermalCameraI2CAddress);

            // バス速度を設定(FastMode：400 kHz)(指定しないと、標準設定(StandardMode：100kHz)になる)
            settings.BusSpeed = I2cBusSpeed.FastMode;

            // 取得したI2Cデバイスと作成した設定で、I2cDeviceのインスタンスを作成
            I2CThermalCamera = await I2cDevice.FromIdAsync(dis[0].Id, settings);

            if (I2CThermalCamera == null)
            {
                Debug.WriteLine(string.Format("スレーブアドレス {0} の I2C コントローラー {1} はほかのアプリで使用されています。他のアプリで使用されていないか、確認してください。", settings.SlaveAddress, dis[0].Id));
                return;
            }

            // サーマルカメラの設定
            try
            {
                // コントロールレジスタを取得
                var ctrreg = ReadRegisterData(0x800D, 1).FirstOrDefault();

                // リフレッシュレートを変更する(現在のコントロールレジスタを読み出して、そいつに対して変更を実施)
                var ctrregset = (ushort)(ctrreg | 0x0380);
                WriteRegisterData(0x800D, ctrregset);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("デバイスとの通信に失敗しました。: " + ex.Message);
                return;
            }

            // EEPROM読み出し
            this.MLX90640_DumpEE();
        }

        /// <summary>
        /// 温度データを取得
        /// </summary>
        /// <returns></returns>
        public double[] GetTemperatureData()
        {
            for (int i = 0; i < 2; i++)
            {
                byte isReady = 0;
                while (isReady == 0)
                {
                    // ステータスレジスタ取得
                    isReady = (byte)(ReadRegisterData(0x8000, 1).FirstOrDefault() & 0x0008);
                }

                //// ステータスレジスタ書き込み(MeasurementStartをON)
                WriteRegisterData(0x8000, 0x0030);
                
                // IRデータ取得
                // 0x0400～0x06FF：IRデータ
                // 0x0700～0x070F：Ta_Vbe、CP.GAIN
                // 0x0720～Ta_PATA,CP,VddPix
                var frameDataS = ReadRegisterData(0x0400, FrameDataLength);

                // ステータスレジスタ読み出し(SubPage番号)
                StatusRegister = (ushort)(ReadRegisterData(0x8000, 1).FirstOrDefault() & 0x0001);

                // コントロールレジスタ読み出し
                ControlRegister = ReadRegisterData(0x800D, 1).FirstOrDefault();

                /////////////////////////////////////////////////////////////////
                // データ読み出しはここまでで終了、データから温度への変換計算実施
                /////////////////////////////////////////////////////////////////

                // 変換に必要なパラメータを計算
                var ta = this.MLX90640_GetTa(frameDataS, CamParameters);
                double tr = ta - 8;

                // 変換後の温度データ保存用配列
                double[] ret = new double[FrameDataLength];

                // 生データを温度データに変換
                MLX90640_CalculateTo(frameDataS, CamParameters, 0.95, tr, ret);

                for (int l = 0; l < frameDataS.Length; l++)
                {
                    if (ret[l] > 0.0)
                    {
                        TotalFrameData[l] = ret[l];
                    }
                }
            }

            return TotalFrameData;
        }

        /// <summary>
        /// 画像データを取得
        /// ※GetTemperatureDataで取った温度データを元に画像を作るので
        /// ※直前にGetTemperatureData()を実行している必要あり
        /// </summary>
        /// <returns></returns>
        //public async Task<BitmapImage> GetWriteableBitmap()
        //{
        //    return await DoubleToRaindowColor(TotalFrameData);
        //}

        #endregion

        #region レジスタアクセス関連
        /// <summary>
        /// EEPROMからレジスタデータ読み出し
        /// </summary>
        /// <param name="readAddr">読み出したいレジスタアドレス</param>
        /// <param name="NumberOfData">データの個数(ushortのデータの個数)</param>
        /// <returns>読み出したデータ(ushort[])</returns>
        private ushort[] ReadRegisterData(ushort readAddr, int NumberOfData)
        {
            // 返すデータ(受信したbyteデータをushortに直したもの)
            ushort[] ret = new ushort[NumberOfData];

            // アドレスを上位/下位に分解
            var destAddr = new byte[] { (byte)(readAddr / 0x100), (byte)(readAddr % 0x100) };
            // 受信用バッファを確保(このサーマルカメラのレジスタは1つで2バイト)
            var readBuf = new byte[NumberOfData * 2];

            // 読み込み実施
            I2CThermalCamera.WriteRead(destAddr, readBuf);

            // 読み込んだbyteデータをushortに直す
            for (int i = 0; i < NumberOfData; i++)
            {
                ret[i] = (ushort)(readBuf[2 * i] * 0x100 + readBuf[2 * i + 1]);
            }
            
            return ret;
        }

        private void WriteRegisterData(ushort writeAddr, ushort data)
        {
            // 書き込むデータ作成(最初の2バイトが書き込み先アドレス、その後の2バイトがそこに書き込むデータ)
            var writeByteData = new byte[]
            {
                (byte)(writeAddr / 0x100),  (byte)(writeAddr % 0x100),      // 書き込み先アドレス
                (byte)(data / 0x100),       (byte)(data % 0x100),           // 書き込みデータ
            };

            // 書き込み実施
            I2CThermalCamera.Write(writeByteData);
        }

        #endregion

        #region EEPROMアクセス関連
        /// <summary>
        /// EEPROMからデータを読み出す
        /// 0x2400から832(0x0340)バイト
        /// </summary>
        private void MLX90640_DumpEE()
        {
            var eeDataS = ReadRegisterData(0x2400, EepromDaataLength);
            MLX90640_ExtractParameters(eeDataS, CamParameters);
        }

        private int MLX90640_ExtractParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            int error = CheckEEPROMValid(eeData);

            if (error == 0)
            {
                ExtractVDDParameters(eeData, mlx90640);
                ExtractPTATParameters(eeData, mlx90640);
                ExtractGainParameters(eeData, mlx90640);
                ExtractTgcParameters(eeData, mlx90640);
                ExtractResolutionParameters(eeData, mlx90640);
                ExtractKsTaParameters(eeData, mlx90640);
                ExtractKsToParameters(eeData, mlx90640);
                ExtractAlphaParameters(eeData, mlx90640);
                ExtractOffsetParameters(eeData, mlx90640);
                ExtractKtaPixelParameters(eeData, mlx90640);
                ExtractKvPixelParameters(eeData, mlx90640);
                ExtractCPParameters(eeData, mlx90640);
                ExtractCILCParameters(eeData, mlx90640);
                error = ExtractDeviatingPixels(eeData, mlx90640);
            }

            return error;
        }

        private int CheckEEPROMValid(ushort[] eeData)
        {
            int deviceSelect;
            deviceSelect = eeData[10] & 0x0040;
            if (deviceSelect == 0)
            {
                return 0;
            }

            return -7;
        }

        private void ExtractVDDParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            short kVdd;
            short vdd25;

            kVdd = (short)eeData[51];

            kVdd = (short)((eeData[51] & 0xFF00) >> 8);
            if (kVdd > 127)
            {
                kVdd = (short)(kVdd - 256);
            }
            kVdd = (short)(32 * kVdd);
            vdd25 = (short)(eeData[51] & 0x00FF);
            vdd25 = (short)(((vdd25 - 256) << 5) - 8192);

            mlx90640.kVdd = kVdd;
            mlx90640.vdd25 = vdd25;
        }

        //------------------------------------------------------------------------------

        private void ExtractPTATParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            float KvPTAT;
            float KtPTAT;
            short vPTAT25;
            double alphaPTAT;

            KvPTAT = (eeData[50] & 0xFC00) >> 10;
            if (KvPTAT > 31)
            {
                KvPTAT = KvPTAT - 64;
            }
            KvPTAT = KvPTAT / 4096;

            KtPTAT = eeData[50] & 0x03FF;
            if (KtPTAT > 511)
            {
                KtPTAT = KtPTAT - 1024;
            }
            KtPTAT = KtPTAT / 8;

            vPTAT25 = (short)(eeData[49]);

            alphaPTAT = (eeData[16] & 0xF000) / Math.Pow(2, (double)14) + 8.0f;

            mlx90640.KvPTAT = KvPTAT;
            mlx90640.KtPTAT = KtPTAT;
            mlx90640.vPTAT25 = (ushort)vPTAT25;
            mlx90640.alphaPTAT = alphaPTAT;
        }

        private void ExtractGainParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            short gainEE;

            gainEE = (short)(eeData[48]);
            if (gainEE > 32767)
            {
                gainEE = (short)(gainEE - 65536);
            }

            mlx90640.gainEE = gainEE;
        }

        private void ExtractTgcParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            float tgc;
            tgc = eeData[60] & 0x00FF;
            if (tgc > 127)
            {
                tgc = tgc - 256;
            }
            tgc = tgc / 32.0f;

            mlx90640.tgc = tgc;
        }

        private void ExtractResolutionParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            uint resolutionEE;
            resolutionEE = (uint)((eeData[56] & 0x3000) >> 12);

            mlx90640.resolutionEE = resolutionEE;
        }

        private void ExtractKsTaParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            float KsTa;
            KsTa = (eeData[60] & 0xFF00) >> 8;
            if (KsTa > 127)
            {
                KsTa = KsTa - 256;
            }
            KsTa = KsTa / 8192.0f;

            mlx90640.KsTa = KsTa;
        }

        private void ExtractKsToParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            int KsToScale;
            byte step;

            step = (byte)(((eeData[63] & 0x3000) >> 12) * 10);

            mlx90640.ct[0] = -40;
            mlx90640.ct[1] = 0;
            mlx90640.ct[2] = (short)((eeData[63] & 0x00F0) >> 4);
            mlx90640.ct[3] = (short)((eeData[63] & 0x0F00) >> 8);

            mlx90640.ct[2] = (short)(mlx90640.ct[2] * step);
            mlx90640.ct[3] = (short)(mlx90640.ct[2] + mlx90640.ct[3] * step);

            KsToScale = (eeData[63] & 0x000F) + 8;
            KsToScale = 1 << KsToScale;

            mlx90640.ksTo[0] = eeData[61] & 0x00FF;
            mlx90640.ksTo[1] = (eeData[61] & 0xFF00) >> 8;
            mlx90640.ksTo[2] = eeData[62] & 0x00FF;
            mlx90640.ksTo[3] = (eeData[62] & 0xFF00) >> 8;


            for (int i = 0; i < 4; i++)
            {
                if (mlx90640.ksTo[i] > 127)
                {
                    mlx90640.ksTo[i] = mlx90640.ksTo[i] - 256;
                }
                mlx90640.ksTo[i] = mlx90640.ksTo[i] / KsToScale;
            }
        }

        private void ExtractAlphaParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            int[] accRow = new int[24];
            int[] accColumn = new int[32];
            int p = 0;
            int alphaRef;
            uint alphaScale;
            uint accRowScale;
            uint accColumnScale;
            uint accRemScale;


            accRemScale = (uint)(eeData[32] & 0x000F);
            accColumnScale = (uint)((eeData[32] & 0x00F0) >> 4);
            accRowScale = (uint)((eeData[32] & 0x0F00) >> 8);
            alphaScale = (uint)(((eeData[32] & 0xF000) >> 12) + 30);
            alphaRef = (eeData[33]);

            for (int i = 0; i < 6; i++)
            {
                p = i * 4;
                accRow[p + 0] = (eeData[34 + i] & 0x000F);
                accRow[p + 1] = (eeData[34 + i] & 0x00F0) >> 4;
                accRow[p + 2] = (eeData[34 + i] & 0x0F00) >> 8;
                accRow[p + 3] = (eeData[34 + i] & 0xF000) >> 12;
            }

            for (int i = 0; i < 24; i++)
            {
                if (accRow[i] > 7)
                {
                    accRow[i] = accRow[i] - 16;
                }
            }

            for (int i = 0; i < 8; i++)
            {
                p = i * 4;
                accColumn[p + 0] = (eeData[40 + i] & 0x000F);
                accColumn[p + 1] = (eeData[40 + i] & 0x00F0) >> 4;
                accColumn[p + 2] = (eeData[40 + i] & 0x0F00) >> 8;
                accColumn[p + 3] = (eeData[40 + i] & 0xF000) >> 12;
            }

            for (int i = 0; i < 32; i++)
            {
                if (accColumn[i] > 7)
                {
                    accColumn[i] = accColumn[i] - 16;
                }
            }

            for (int i = 0; i < 24; i++)
            {
                for (int j = 0; j < 32; j++)
                {
                    p = 32 * i + j;
                    mlx90640.alpha[p] = (eeData[64 + p] & 0x03F0) >> 4;
                    if (mlx90640.alpha[p] > 31)
                    {
                        mlx90640.alpha[p] = mlx90640.alpha[p] - 64;
                    }
                    mlx90640.alpha[p] = mlx90640.alpha[p] * (1 << (int)accRemScale);
                    mlx90640.alpha[p] = (alphaRef + (accRow[i] << (int)accRowScale) + (accColumn[j] << (int)accColumnScale) + mlx90640.alpha[p]);
                    mlx90640.alpha[p] = mlx90640.alpha[p] / Math.Pow(2, (double)alphaScale);
                }
            }
        }

        private void ExtractOffsetParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            int[] occRow = new int[24];
            int[] occColumn = new int[32];
            int p = 0;
            short offsetRef;
            uint occRowScale;
            uint occColumnScale;
            uint occRemScale;


            occRemScale = (uint)(eeData[16] & 0x000F);
            occColumnScale = (uint)((eeData[16] & 0x00F0) >> 4);
            occRowScale = (uint)((eeData[16] & 0x0F00) >> 8);
            offsetRef = (short)(eeData[17]);
            if (offsetRef > 32767)
            {
                offsetRef = (short)(offsetRef - 65536);
            }

            for (int i = 0; i < 6; i++)
            {
                p = i * 4;
                occRow[p + 0] = (eeData[18 + i] & 0x000F);
                occRow[p + 1] = (eeData[18 + i] & 0x00F0) >> 4;
                occRow[p + 2] = (eeData[18 + i] & 0x0F00) >> 8;
                occRow[p + 3] = (eeData[18 + i] & 0xF000) >> 12;
            }

            for (int i = 0; i < 24; i++)
            {
                if (occRow[i] > 7)
                {
                    occRow[i] = occRow[i] - 16;
                }
            }

            for (int i = 0; i < 8; i++)
            {
                p = i * 4;
                occColumn[p + 0] = (eeData[24 + i] & 0x000F);
                occColumn[p + 1] = (eeData[24 + i] & 0x00F0) >> 4;
                occColumn[p + 2] = (eeData[24 + i] & 0x0F00) >> 8;
                occColumn[p + 3] = (eeData[24 + i] & 0xF000) >> 12;
            }

            for (int i = 0; i < 32; i++)
            {
                if (occColumn[i] > 7)
                {
                    occColumn[i] = occColumn[i] - 16;
                }
            }

            for (int i = 0; i < 24; i++)
            {
                for (int j = 0; j < 32; j++)
                {
                    p = 32 * i + j;
                    mlx90640.offset[p] = (short)((eeData[64 + p] & 0xFC00) >> 10);
                    if (mlx90640.offset[p] > 31)
                    {
                        mlx90640.offset[p] = (short)(mlx90640.offset[p] - 64);
                    }
                    mlx90640.offset[p] = (short)(mlx90640.offset[p] * (1 << (int)occRemScale));
                    mlx90640.offset[p] = (short)((offsetRef + (occRow[i] << (int)occRowScale) + (occColumn[j] << (int)occColumnScale) + mlx90640.offset[p]));
                }
            }
        }

        private void ExtractKtaPixelParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            int p = 0;
            byte[] KtaRC = new byte[4];
            byte KtaRoCo;
            byte KtaRoCe;
            byte KtaReCo;
            byte KtaReCe;
            uint ktaScale1;
            uint ktaScale2;
            uint split;

            KtaRoCo = (byte)((eeData[54] & 0xFF00) >> 8);
            if (KtaRoCo > 127)
            {
                KtaRoCo = (byte)(KtaRoCo - 256);
            }
            KtaRC[0] = KtaRoCo;

            KtaReCo = (byte)(eeData[54] & 0x00FF);
            if (KtaReCo > 127)
            {
                KtaReCo = (byte)(KtaReCo - 256);
            }
            KtaRC[2] = KtaReCo;

            KtaRoCe = (byte)((eeData[55] & 0xFF00) >> 8);
            if (KtaRoCe > 127)
            {
                KtaRoCe = (byte)(KtaRoCe - 256);
            }
            KtaRC[1] = KtaRoCe;

            KtaReCe = (byte)((eeData[55] & 0x00FF));
            if (KtaReCe > 127)
            {
                KtaReCe = (byte)(KtaReCe - 256);
            }
            KtaRC[3] = KtaReCe;

            ktaScale1 = (uint)(((eeData[56] & 0x00F0) >> 4) + 8);
            ktaScale2 = (uint)((eeData[56] & 0x000F));

            for (int i = 0; i < 24; i++)
            {
                for (int j = 0; j < 32; j++)
                {
                    p = 32 * i + j;
                    split = (uint)(2 * (p / 32 - (p / 64) * 2) + p % 2);
                    mlx90640.kta[p] = (eeData[64 + p] & 0x000E) >> 1;
                    if (mlx90640.kta[p] > 3)
                    {
                        mlx90640.kta[p] = mlx90640.kta[p] - 8;
                    }
                    mlx90640.kta[p] = mlx90640.kta[p] * (1 << (int)ktaScale2);
                    mlx90640.kta[p] = KtaRC[split] + mlx90640.kta[p];
                    mlx90640.kta[p] = mlx90640.kta[p] / Math.Pow(2, (double)ktaScale1);
                }
            }
        }

        private void ExtractKvPixelParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            int p = 0;
            byte[] KvT = new byte[4];
            byte KvRoCo;
            byte KvRoCe;
            byte KvReCo;
            byte KvReCe;
            uint kvScale;
            uint split;

            KvRoCo = (byte)((eeData[52] & 0xF000) >> 12);
            if (KvRoCo > 7)
            {
                KvRoCo = (byte)(KvRoCo - 16);
            }
            KvT[0] = KvRoCo;

            KvReCo = (byte)((eeData[52] & 0x0F00) >> 8);
            if (KvReCo > 7)
            {
                KvReCo = (byte)(KvReCo - 16);
            }
            KvT[2] = KvReCo;

            KvRoCe = (byte)((eeData[52] & 0x00F0) >> 4);
            if (KvRoCe > 7)
            {
                KvRoCe = (byte)(KvRoCe - 16);
            }
            KvT[1] = KvRoCe;

            KvReCe = (byte)((eeData[52] & 0x000F));
            if (KvReCe > 7)
            {
                KvReCe = (byte)(KvReCe - 16);
            }
            KvT[3] = KvReCe;

            kvScale = (uint)((eeData[56] & 0x0F00) >> 8);


            for (int i = 0; i < 24; i++)
            {
                for (int j = 0; j < 32; j++)
                {
                    p = 32 * i + j;
                    split = (uint)(2 * (p / 32 - (p / 64) * 2) + p % 2);
                    mlx90640.kv[p] = KvT[split];
                    mlx90640.kv[p] = mlx90640.kv[p] / Math.Pow(2, (double)kvScale);
                }
            }
        }

        private void ExtractCPParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            double[] alphaSP = new double[2];
            short[] offsetSP = new short[2];
            double cpKv;
            double cpKta;
            uint alphaScale;
            uint ktaScale1;
            uint kvScale;

            alphaScale = (uint)(((eeData[32] & 0xF000) >> 12) + 27);

            offsetSP[0] = (short)((eeData[58] & 0x03FF));
            if (offsetSP[0] > 511)
            {
                offsetSP[0] = (short)(offsetSP[0] - 1024);
            }

            offsetSP[1] = (short)((eeData[58] & 0xFC00) >> 10);
            if (offsetSP[1] > 31)
            {
                offsetSP[1] = (short)(offsetSP[1] - 64);
            }
            offsetSP[1] = (short)(offsetSP[1] + offsetSP[0]);

            alphaSP[0] = (eeData[57] & 0x03FF);
            if (alphaSP[0] > 511)
            {
                alphaSP[0] = alphaSP[0] - 1024;
            }
            alphaSP[0] = alphaSP[0] / Math.Pow(2, (double)alphaScale);

            alphaSP[1] = (eeData[57] & 0xFC00) >> 10;
            if (alphaSP[1] > 31)
            {
                alphaSP[1] = alphaSP[1] - 64;
            }
            alphaSP[1] = (1 + alphaSP[1] / 128) * alphaSP[0];

            cpKta = (eeData[59] & 0x00FF);
            if (cpKta > 127)
            {
                cpKta = cpKta - 256;
            }
            ktaScale1 = (uint)(((eeData[56] & 0x00F0) >> 4) + 8);
            mlx90640.cpKta = cpKta / Math.Pow(2, (double)ktaScale1);

            cpKv = (eeData[59] & 0xFF00) >> 8;
            if (cpKv > 127)
            {
                cpKv = cpKv - 256;
            }
            kvScale = (uint)((eeData[56] & 0x0F00) >> 8);
            mlx90640.cpKv = cpKv / Math.Pow(2, (double)kvScale);

            mlx90640.cpAlpha[0] = alphaSP[0];
            mlx90640.cpAlpha[1] = alphaSP[1];
            mlx90640.cpOffset[0] = offsetSP[0];
            mlx90640.cpOffset[1] = offsetSP[1];
        }

        private void ExtractCILCParameters(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            float[] ilChessC = new float[3];
            uint calibrationModeEE;

            calibrationModeEE = (uint)((eeData[10] & 0x0800) >> 4);
            calibrationModeEE = calibrationModeEE ^ 0x80;

            ilChessC[0] = (eeData[53] & 0x003F);
            if (ilChessC[0] > 31)
            {
                ilChessC[0] = ilChessC[0] - 64;
            }
            ilChessC[0] = ilChessC[0] / 16.0f;

            ilChessC[1] = (eeData[53] & 0x07C0) >> 6;
            if (ilChessC[1] > 15)
            {
                ilChessC[1] = ilChessC[1] - 32;
            }
            ilChessC[1] = ilChessC[1] / 2.0f;

            ilChessC[2] = (eeData[53] & 0xF800) >> 11;
            if (ilChessC[2] > 15)
            {
                ilChessC[2] = ilChessC[2] - 32;
            }
            ilChessC[2] = ilChessC[2] / 8.0f;

            mlx90640.calibrationModeEE = calibrationModeEE;
            mlx90640.ilChessC[0] = ilChessC[0];
            mlx90640.ilChessC[1] = ilChessC[1];
            mlx90640.ilChessC[2] = ilChessC[2];
        }

        private int ExtractDeviatingPixels(ushort[] eeData, ParamsMLX90640 mlx90640)
        {
            ushort pixCnt = 0;
            ushort brokenPixCnt = 0;
            ushort outlierPixCnt = 0;
            int warn = 0;
            int i;

            for (pixCnt = 0; pixCnt < 5; pixCnt++)
            {
                mlx90640.brokenPixels[pixCnt] = 0xFFFF;
                mlx90640.outlierPixels[pixCnt] = 0xFFFF;
            }

            pixCnt = 0;
            while (pixCnt < 768 && brokenPixCnt < 5 && outlierPixCnt < 5)
            {
                if (eeData[pixCnt + 64] == 0)
                {
                    mlx90640.brokenPixels[brokenPixCnt] = pixCnt;
                    brokenPixCnt = (ushort)(brokenPixCnt + 1);
                }
                else if ((eeData[pixCnt + 64] & 0x0001) != 0)
                {
                    mlx90640.outlierPixels[outlierPixCnt] = pixCnt;
                    outlierPixCnt = (ushort)(outlierPixCnt + 1);
                }

                pixCnt = (ushort)(pixCnt + 1);

            }

            if (brokenPixCnt > 4)
            {
                warn = -3;
            }
            else if (outlierPixCnt > 4)
            {
                warn = -4;
            }
            else if ((brokenPixCnt + outlierPixCnt) > 4)
            {
                warn = -5;
            }
            else
            {
                for (pixCnt = 0; pixCnt < brokenPixCnt; pixCnt++)
                {
                    for (i = pixCnt + 1; i < brokenPixCnt; i++)
                    {
                        warn = CheckAdjacentPixels(mlx90640.brokenPixels[pixCnt], mlx90640.brokenPixels[i]);
                        if (warn != 0)
                        {
                            return warn;
                        }
                    }
                }

                for (pixCnt = 0; pixCnt < outlierPixCnt; pixCnt++)
                {
                    for (i = pixCnt + 1; i < outlierPixCnt; i++)
                    {
                        warn = CheckAdjacentPixels(mlx90640.outlierPixels[pixCnt], mlx90640.outlierPixels[i]);
                        if (warn != 0)
                        {
                            return warn;
                        }
                    }
                }

                for (pixCnt = 0; pixCnt < brokenPixCnt; pixCnt++)
                {
                    for (i = 0; i < outlierPixCnt; i++)
                    {
                        warn = CheckAdjacentPixels(mlx90640.brokenPixels[pixCnt], mlx90640.outlierPixels[i]);
                        if (warn != 0)
                        {
                            return warn;
                        }
                    }
                }

            }


            return warn;

        }

        private int CheckAdjacentPixels(ushort pix1, ushort pix2)
        {
            int pixPosDif;

            pixPosDif = pix1 - pix2;
            if (pixPosDif > -34 && pixPosDif < -30)
            {
                return -6;
            }
            if (pixPosDif > -2 && pixPosDif < 2)
            {
                return -6;
            }
            if (pixPosDif > 30 && pixPosDif < 34)
            {
                return -6;
            }

            return 0;
        }
        #endregion

        #region 読み出したフレームデータの変換関連

        private double MLX90640_GetVdd(ushort[] frameData, ParamsMLX90640 parameters)
        {
            double vdd;
            double resolutionCorrection;

            int resolutionRAM;

            vdd = frameData[810];
            if (vdd > 32767)
            {
                vdd = vdd - 65536;
            }
            //resolutionRAM = (frameData[832] & 0x0C00) >> 10;
            resolutionRAM = (ControlRegister & 0x0C00) >> 10;
            resolutionCorrection = (double)(Math.Pow(2, parameters.resolutionEE) / Math.Pow(2, resolutionRAM));
            vdd = (float)(((resolutionCorrection * vdd) - parameters.vdd25) / parameters.kVdd + 3.3);

            return vdd;
        }

        private double MLX90640_GetTa(ushort[] frameData, ParamsMLX90640 parameters)
        {
            double ptat;
            double ptatArt;
            double vdd;
            double ta;

            vdd = MLX90640_GetVdd(frameData, parameters);

            ptat = frameData[800];
            if (ptat > 32767)
            {
                ptat = ptat - 65536;
            }

            ptatArt = frameData[768];
            if (ptatArt > 32767)
            {
                ptatArt = ptatArt - 65536;
            }
            ptatArt = (ptat / (ptat * parameters.alphaPTAT + ptatArt)) * Math.Pow(2, (double)18);

            ta = (ptatArt / (1 + parameters.KvPTAT * (vdd - 3.3)) - parameters.vPTAT25);
            ta = ta / parameters.KtPTAT + 25;

            return ta;
        }

        private void MLX90640_CalculateTo(ushort[] frameData, ParamsMLX90640 parameters, double emissivity, double tr, double[] result)
        {
            double vdd;
            double ta;
            double ta4;
            double tr4;
            double taTr;
            double gain;
            double[] irDataCP = new double[2];
            double irData;
            double alphaCompensated;
            byte mode;
            byte ilPattern;
            byte chessPattern;
            byte pattern;
            byte conversionPattern;
            double Sx;
            double To;
            double[] alphaCorrR = new double[4];
            byte range;
            ushort subPage;

            //subPage = frameData[833];
            subPage = StatusRegister;
            vdd = MLX90640_GetVdd(frameData, parameters);
            ta = MLX90640_GetTa(frameData, parameters);
            ta4 = Math.Pow((ta + 273.15), (double)4);
            tr4 = Math.Pow((tr + 273.15), (double)4);
            taTr = tr4 - (tr4 - ta4) / emissivity;

            alphaCorrR[0] = 1 / (1 + parameters.ksTo[0] * 40);
            alphaCorrR[1] = 1;
            alphaCorrR[2] = (1 + parameters.ksTo[2] * parameters.ct[2]);
            alphaCorrR[3] = alphaCorrR[2] * (1 + parameters.ksTo[3] * (parameters.ct[3] - parameters.ct[2]));

            //------------------------- Gain calculation -----------------------------------    
            gain = frameData[778];
            if (gain > 32767)
            {
                gain = gain - 65536;
            }

            gain = parameters.gainEE / gain;

            //------------------------- To calculation -------------------------------------    
            //mode = (byte)((frameData[832] & 0x1000) >> 5);
            mode = (byte)((ControlRegister & 0x1000) >> 5);

            irDataCP[0] = frameData[776];
            irDataCP[1] = frameData[808];
            for (int i = 0; i < 2; i++)
            {
                if (irDataCP[i] > 32767)
                {
                    irDataCP[i] = irDataCP[i] - 65536;
                }
                irDataCP[i] = irDataCP[i] * gain;
            }
            irDataCP[0] = irDataCP[0] - parameters.cpOffset[0] * (1 + parameters.cpKta * (ta - 25)) * (1 + parameters.cpKv * (vdd - 3.3));
            if (mode == parameters.calibrationModeEE)
            {
                irDataCP[1] = irDataCP[1] - parameters.cpOffset[1] * (1 + parameters.cpKta * (ta - 25)) * (1 + parameters.cpKv * (vdd - 3.3));
            }
            else
            {
                irDataCP[1] = irDataCP[1] - (parameters.cpOffset[1] + parameters.ilChessC[0]) * (1 + parameters.cpKta * (ta - 25)) * (1 + parameters.cpKv * (vdd - 3.3));
            }

            for (int pixelNumber = 0; pixelNumber < 768; pixelNumber++)
            {
                ilPattern = (byte)(pixelNumber / 32 - (pixelNumber / 64) * 2);
                chessPattern = (byte)(ilPattern ^ (pixelNumber - (pixelNumber / 2) * 2));
                conversionPattern = (byte)(((pixelNumber + 2) / 4 - (pixelNumber + 3) / 4 + (pixelNumber + 1) / 4 - pixelNumber / 4) * (1 - 2 * ilPattern));

                if (mode == 0)
                {
                    pattern = ilPattern;
                }
                else
                {
                    pattern = chessPattern;
                }

                //if (pattern == frameData[833])
                if (pattern == StatusRegister)
                {
                    irData = frameData[pixelNumber];
                    if (irData > 32767)
                    {
                        irData = irData - 65536;
                    }
                    irData = irData * gain;

                    irData = irData - parameters.offset[pixelNumber] * (1 + parameters.kta[pixelNumber] * (ta - 25)) * (1 + parameters.kv[pixelNumber] * (vdd - 3.3));
                    if (mode != parameters.calibrationModeEE)
                    {
                        irData = irData + parameters.ilChessC[2] * (2 * ilPattern - 1) - parameters.ilChessC[1] * conversionPattern;
                    }

                    irData = irData / emissivity;

                    irData = irData - parameters.tgc * irDataCP[subPage];

                    alphaCompensated = (parameters.alpha[pixelNumber] - parameters.tgc * parameters.cpAlpha[subPage]) * (1 + parameters.KsTa * (ta - 25));

                    Sx = Math.Pow((double)alphaCompensated, (double)3) * (irData + alphaCompensated * taTr);
                    Sx = Math.Sqrt(Math.Sqrt(Sx)) * parameters.ksTo[1];

                    To = Math.Sqrt(Math.Sqrt(irData / (alphaCompensated * (1 - parameters.ksTo[1] * 273.15) + Sx) + taTr)) - 273.15;

                    if (To < parameters.ct[1])
                    {
                        range = 0;
                    }
                    else if (To < parameters.ct[2])
                    {
                        range = 1;
                    }
                    else if (To < parameters.ct[3])
                    {
                        range = 2;
                    }
                    else
                    {
                        range = 3;
                    }

                    To = Math.Sqrt(Math.Sqrt(irData / (alphaCompensated * alphaCorrR[range] * (1 + parameters.ksTo[range] * (To - parameters.ct[range]))) + taTr)) - 273.15;

                    result[pixelNumber] = To;
                }
            }
        }

        #endregion

        #region 画像作成関連

        #endregion
    }


    class ParamsMLX90640
    {
        public short kVdd;
        public short vdd25;
        public float KvPTAT;
        public float KtPTAT;
        public ushort vPTAT25;
        public double alphaPTAT;
        public short gainEE;
        public float tgc;
        public double cpKv;
        public double cpKta;
        public uint resolutionEE;
        public uint calibrationModeEE;
        public float KsTa;
        public float[] ksTo = new float[4];
        public short[] ct = new short[4];
        public double[] alpha = new double[768];
        public short[] offset = new short[768];
        public double[] kta = new double[768];
        public double[] kv = new double[768];
        public double[] cpAlpha = new double[2];
        public short[] cpOffset = new short[2];
        public double[] ilChessC = new double[3];
        public ushort[] brokenPixels = new ushort[5];
        public ushort[] outlierPixels = new ushort[5];
    }


}
