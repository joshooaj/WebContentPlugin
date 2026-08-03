using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
            return $"{method.DeclaringType.FullName}.{method.Name}";
        }
    }

    internal static class SiteLicenseHandler
    {
        private static readonly object SyncRoot = new object();
        private static readonly Guid PluginGuid = new Guid(Resources.PluginId);
        private static readonly Regex IsFreeRegex = new Regex(@"Test|Essential", RegexOptions.IgnoreCase);

        private static object _licenseReceiver;
        private static LicenseInformation _activatedLicense;
        private static LicenseInformation _myLicense;
        private static bool _initialized;
        private static string _productName;
        private static bool _isFree;

        public static event EventHandler LicenseActivated;

        public static bool IsFree
        {
            get
            {
                if (EnvironmentManager.Instance.SmartClientInOfflineMode) return true;

                lock (SyncRoot)
                {
                    if (!string.IsNullOrEmpty(_productName))
                    {
                        return _isFree;
                    }

                    try
                    {
                        var siteFqid = EnvironmentManager.Instance?.CurrentSite;
                        if (siteFqid == null)
                        {
                            _productName = "NotAvailable";
                            Logger.Log("CurrentSite is null, skipping product name check.");
                            return _isFree;
                        }

                        var item = Configuration.Instance.GetItem(siteFqid);
                        if (item?.Properties != null && item.Properties.TryGetValue("ProductName", out var productName) && !string.IsNullOrWhiteSpace(productName))
                        {
                            _productName = productName;
                            _isFree = IsFreeRegex.IsMatch(productName);
                            Logger.Log($"Product name detected: \"{_productName}\", free={_isFree}");
                        }
                        else
                        {
                            _productName = "NotAvailable";
                        }
                    }
                    catch (Exception ex)
                    {
                        _productName = "NotAvailable";
                        Logger.Log($"Error checking product name: {ex.Message}");
                    }

                    return _isFree;
                }
            }
        }

        public static bool IsActivated
        {
            get
            {
                EnsureInitialized();
                if (IsFree) return true;

                lock (SyncRoot)
                {
                    return _activatedLicense != null;
                }
            }
        }

        public static bool IsExpired
        {
            get
            {
                EnsureInitialized();
                if (IsFree) return false;
                return !IsActivated && Expires <= DateTime.UtcNow;
            }
        }

        public static DateTime Expires
        {
            get
            {
                EnsureInitialized();
                if (IsFree) return DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

                lock (SyncRoot)
                {
                    return _myLicense == null
                        ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
                        : DateTime.SpecifyKind(_myLicense.Expire, DateTimeKind.Utc);
                }
            }
        }

        internal static void Init()
        {
            lock (SyncRoot)
            {
                if (_initialized)
                {
                    return;
                }

                try
                {
                    _licenseReceiver = EnvironmentManager.Instance?.RegisterReceiver(NewLicense, new MessageIdFilter(MessageId.System.LicenseChangedIndication));
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to register license receiver: {ex.Message}");
                }

                _activatedLicense = TryGetActivatedLicense();
                _myLicense = TryGetReservedLicense();

                if (_myLicense == null)
                {
                    _myLicense = _activatedLicense?.Clone() ?? CreateTrialLicense();
                    TrySaveLicenseInformation(_myLicense, nameof(Init));
                }

                _initialized = true;
                if (IsFree)
                {
                    Logger.Log($"License check is disabled based on product name \"{_productName}\".");
                }
            }
        }

        internal static void Close()
        {
            lock (SyncRoot)
            {
                Logger.Log("Closing license handler.");
                if (_licenseReceiver != null)
                {
                    try
                    {
                        EnvironmentManager.Instance.UnRegisterReceiver(_licenseReceiver);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to unregister license receiver: {ex.Message}");
                    }

                    _licenseReceiver = null;
                }

                _initialized = false;
                _productName = null;
                _isFree = false;
            }
        }

        internal static Collection<LicenseInformation> GetLicenseRequest()
        {
            EnsureInitialized();

            lock (SyncRoot)
            {
                if (_myLicense == null)
                {
                    _myLicense = _activatedLicense?.Clone() ?? CreateTrialLicense();
                }

                Logger.Log("Generating license request.");
                _myLicense.Counter = 1;
                return new Collection<LicenseInformation> { _myLicense };
            }
        }

        private static object NewLicense(Message message, FQID destination, FQID sender)
        {
            lock (SyncRoot)
            {
                var response = TryGetActivatedLicense();
                if (response == null)
                {
                    _activatedLicense = null;
                    if (_myLicense == null)
                    {
                        _myLicense = CreateTrialLicense();
                    }

                    Logger.Log("License changed indication received, but no active license was returned.");
                    OnLicenseActivated();
                    return null;
                }

                Logger.Log($"License changed indication received. License type: {response.LicenseType}, expires: {response.Expire}");
                response.Counter = response.Counter > 1 ? 1 : response.Counter;
                _activatedLicense = response.Clone();
                _myLicense = response.Clone();
                TrySaveLicenseInformation(_myLicense, nameof(NewLicense));
                OnLicenseActivated();
                return null;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            Init();
        }

        private static void OnLicenseActivated()
        {
            LicenseActivated?.Invoke(null, EventArgs.Empty);
        }

        private static LicenseInformation TryGetActivatedLicense()
        {
            try
            {
                return EnvironmentManager.Instance?.LicenseManager.GetLicense(PluginGuid, Resources.PluginLicenseType);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to read active license: {ex.Message}");
                return null;
            }
        }

        private static LicenseInformation TryGetReservedLicense()
        {
            try
            {
                return EnvironmentManager.Instance?.LicenseManager.ReservedLicenseManager.GetLicenseInformation(PluginGuid, Resources.PluginLicenseType);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to read reserved license: {ex.Message}");
                return null;
            }
        }

        private static void TrySaveLicenseInformation(LicenseInformation license, string operation)
        {
            if (!IsAdministrationEnvironment || license == null)
            {
                return;
            }

            try
            {
                EnvironmentManager.Instance.LicenseManager.ReservedLicenseManager.SaveLicenseInformation(license);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to persist reserved license during {operation}: {ex.Message}");
            }
        }

        private static LicenseInformation CreateTrialLicense()
        {
            return new LicenseInformation
            {
                PluginId = PluginGuid,
                Counter = 1,
                CustomData = string.Empty,
                LicenseType = Resources.PluginLicenseType,
                Name = Resources.PluginName,
                Expire = DateTime.UtcNow.AddDays(30),
                TrialMode = true,
                ItemIdentifications = new Collection<LicenseItem>()
            };
        }

        private static bool IsAdministrationEnvironment =>
            EnvironmentManager.Instance != null && EnvironmentManager.Instance.EnvironmentType == EnvironmentType.Administration;
    }
}
