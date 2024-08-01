using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using VideoOS.Platform;
using VideoOS.Platform.License;
using VideoOS.Platform.Messaging;

namespace WebContent.Admin
{
    internal static class Logger
    {
        public static void Log(string message)
        {
            EnvironmentManager.Instance.Log(false, GetLocationName(new StackTrace(1)), message);
        }

        private static string GetLocationName(StackTrace stackTrace)
        {
            var frame = stackTrace.GetFrame(0);
            var method = frame.GetMethod();
            var location = $"{method.DeclaringType.FullName}.{method.Name}";
            return location;
        }
    }

    internal static class SiteLicenseHandler
    {
        private static object _licenseReceiver;
        private static LicenseInformation _activatedLicense;
        private static LicenseInformation _myLicense;
        private static bool _initialized;

        public static event EventHandler LicenseActivated;

        private static void OnLicenseActivated([CallerMemberName] string propertyName = null)
        {
            LicenseActivated?.Invoke(null, EventArgs.Empty);
        }

        private static readonly Regex _isFreeRegex = new Regex(@"Test|Essential", RegexOptions.IgnoreCase);
        private static string _productName;
        private static bool _isFree;
        public static bool IsFree {
            get
            {
                // Free to use in an export
                if (EnvironmentManager.Instance.SmartClientInOfflineMode) return true;
                if (string.IsNullOrEmpty(_productName))
                {
                    var item = Configuration.Instance.GetItem(EnvironmentManager.Instance.CurrentSite);
                    if (item.Properties.ContainsKey("ProductName"))
                    {
                        _productName = item.Properties["ProductName"];
                        _isFree = _isFreeRegex.IsMatch(item.Properties["ProductName"]);
                    }
                    else
                    {
                        Logger.Log("ProductName property not found in site properties.");
                        _productName = "NotAvailable";
                    }
                }
                return _isFree;
            }
        }

        public static bool IsActivated
        {
            get
            {
                if (!_initialized)
                {
                    Init();
                }
                if (IsFree) return true;
                return _activatedLicense != null;
            }
        }

        public static bool IsExpired
        {
            get
            {
                if (!_initialized)
                {
                    Init();
                }
                if (IsFree) return false;
                return !IsActivated && Expires <= DateTime.UtcNow;
            }
        }

        public static DateTime Expires
        {
            get
            {
                if (!_initialized)
                {
                    Init();
                }
                if (IsFree) return DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
                return DateTime.SpecifyKind(_myLicense.Expire, DateTimeKind.Utc);
            }
        }

        internal static void Init()
        {
            _licenseReceiver = EnvironmentManager.Instance?.RegisterReceiver(NewLicense, new MessageIdFilter(MessageId.System.LicenseChangedIndication));

            _activatedLicense = EnvironmentManager.Instance?.LicenseManager.GetLicense(new Guid(Resources.PluginId), Resources.PluginLicenseType);

            _myLicense = EnvironmentManager.Instance?.LicenseManager.ReservedLicenseManager.GetLicenseInformation(new Guid(Resources.PluginId), Resources.PluginLicenseType);

            if (_myLicense == null || _myLicense.Expire > DateTime.UtcNow.AddDays(30))
            {
                if (_activatedLicense != null)
                {
                    _myLicense = _activatedLicense;
                }
                else
                {
                    _myLicense = new LicenseInformation
                    {
                        PluginId = new Guid(Resources.PluginId),
                        Counter = 1,
                        CustomData = string.Empty,
                        LicenseType = Resources.PluginLicenseType,
                        Name = Resources.PluginName,
                        Expire = DateTime.UtcNow.AddDays(30),
                        TrialMode = true,
                        ItemIdentifications = new Collection<LicenseItem>()
                    };
                }

                if (EnvironmentManager.Instance?.EnvironmentType == EnvironmentType.Administration)
                {
                    EnvironmentManager.Instance.LicenseManager.ReservedLicenseManager.SaveLicenseInformation(_myLicense);
                }
            }

            _initialized = true;
            if (IsFree)
            {
                Logger.Log($"License check is disabled based on product name \"{_productName}\"");
            }
        }

        internal static void Close()
        {
            EnvironmentManager.Instance.UnRegisterReceiver(_licenseReceiver);
        }

        private static object NewLicense(Message message, FQID destination, FQID sender)
        {
            var response = EnvironmentManager.Instance.LicenseManager.GetLicense(new Guid(Resources.PluginId), Resources.PluginLicenseType);
            if (response == null) return null;

            response.Counter = response.Counter > 1 ? 1 : response.Counter;
            _activatedLicense = response.Clone();
            _myLicense = response.Clone();
            EnvironmentManager.Instance.LicenseManager.ReservedLicenseManager.SaveLicenseInformation(_myLicense);
            return null;
        }

        internal static Collection<LicenseInformation> GetLicenseRequest()
        {
            if (!_initialized) Init();
            _myLicense.Counter = 1;
            return new Collection<LicenseInformation> { _myLicense };
        }
    }
}
