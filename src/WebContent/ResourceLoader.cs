using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace WebContent
{
    internal static class ResourceLoader
    {
        private static BitmapSource _companyLogo;
        private static BitmapSource _webContentIcon;
        private static Bitmap _companyLogoBitmap;
        private static Bitmap _webContentIconBitmap;

        public static BitmapSource CompanyLogoSource
        {
            get
            {
                if (_companyLogo == null)
                {
                    var uri = new System.Uri("pack://application:,,,/WebContent;component/Resources/CompanyLogo.png");
                    _companyLogo = BitmapFrame.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
                return _companyLogo;
            }
        }

        public static Bitmap CompanyLogo
        {
            get
            {
                if (_companyLogoBitmap == null)
                {
                    _companyLogoBitmap = BitmapSourceToBitmap(CompanyLogoSource);
                }
                return _companyLogoBitmap;
            }
        }

        public static BitmapSource WebContentIconSource
        {
            get
            {
                if (_webContentIcon == null)
                {
                    var uri = new System.Uri("pack://application:,,,/WebContent;component/Resources/WebContentIcon.png");
                    _webContentIcon = BitmapFrame.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
                return _webContentIcon;
            }
        }

        public static Bitmap WebContentIcon
        {
            get
            {
                if (_webContentIconBitmap == null)
                {
                    _webContentIconBitmap = BitmapSourceToBitmap(WebContentIconSource);
                }
                return _webContentIconBitmap;
            }
        }

        private static Bitmap BitmapSourceToBitmap(BitmapSource source)
        {
            using (var stream = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                encoder.Save(stream);
                stream.Position = 0;
                using (var temp = new Bitmap(stream))
                {
                    return new Bitmap(temp);
                }
            }
        }
    }
}
