// Подключаем пространство имён для P/Invoke-вызовов (DllImport, Marshal, StructLayout)
using System.Runtime.InteropServices;
// Подключаем наши модели данных (BluetoothDeviceInfo)
using MauiBluethooth.Models;
// Подключаем интерфейс Bluetooth-сервиса
using MauiBluethooth.Services;

// Объявляем пространство имён Windows-реализаций сервисов
namespace MauiBluethooth.Platforms.Windows.Services;

// Реализация IBluetoothService для платформы Windows
// (использует старый Bluetooth-стек Windows через P/Invoke из библиотеки bthprops.cpl)
public class WindowsBluetoothService : IBluetoothService
{
	// Множитель таймаута опроса устройств (по умолчанию, рекомендованный Windows)
	private const int DefaultInquiryTimeoutMultiplier = 3;

	// Объект-блокировка для потокобезопасного доступа к полю _inquiryRunning
	private readonly object _gate = new();
	// Флаг: идёт ли сейчас опрос (inquiry) устройств
	private bool _inquiryRunning;

	// Событие: найдено новое Bluetooth-устройство
	public event EventHandler<BluetoothDeviceInfo>? DeviceDiscovered;

	// Событие: поиск завершён
	public event EventHandler? DiscoveryFinished;

	// На Windows разрешения спрашивать не нужно — всегда возвращаем успех
	public Task<bool> RequestPermissionsAsync() => Task.FromResult(true);

	// Запуск поиска (опроса) Bluetooth-устройств
	public Task<bool> StartDiscoveryAsync()
	{
		// Блокируем доступ к флагу, чтобы корректно работать из нескольких потоков
		lock (_gate)
		{
			// Если опрос уже запущен
			if (_inquiryRunning)
				// Отказываем в повторном запуске
				return Task.FromResult(false);

			// Устанавливаем флаг «опрос идёт»
			_inquiryRunning = true;
		}

		// Запускаем опрос в фоновом потоке в режиме fire-and-forget 
		// (ContinueWith(static t => {}) — «глотает» исключения, чтобы не уронить процесс)
		_ = Task.Run(RunInquiryAsync).ContinueWith(static t => { });
		// Сообщаем, что опрос успешно запущен
		return Task.FromResult(true);
	}

	// Остановка поиска (асинхронная операция)
	public async Task StopDiscoveryAsync()
	{
		// Блокируем доступ к флагу
		lock (_gate)
		{
			// Сбрасываем флаг — фоновый поток сам выйдет из цикла на следующей проверке
			_inquiryRunning = false;
		}

		// Безусловно завершённая задача (метод async — для совместимости с интерфейсом)
		await Task.CompletedTask;
		// Оповещаем подписчиков о завершении поиска
		DiscoveryFinished?.Invoke(this, EventArgs.Empty);
	}

	// Фоновый метод опроса устройств
	private async Task RunInquiryAsync()
	{
		// Оборачиваем в try/finally, чтобы гарантированно освободить ресурсы
		try
		{
			// Подготавливаем параметры поиска для WinAPI
			var searchParams = new BLUETOOTH_DEVICE_SEARCH_PARAMS
			{
				// Размер структуры в байтах (требование WinAPI)
				dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS)),
				// Возвращать авторизованные устройства
				fReturnAuthenticated = 1,
				// Возвращать запомненные (известные ранее) устройства
				fReturnRemembered = 1,
				// Возвращать неизвестные (новые) устройства
				fReturnUnknown = 1,
				// Возвращать подключённые устройства
				fReturnConnected = 1,
				// Запускать реальное сканирование эфира (inquiry)
				fIssueInquiry = 1,
				// Множитель таймаута опроса
				cTimeoutMultiplier = DefaultInquiryTimeoutMultiplier,
				// Радио-модуль: IntPtr.Zero — использовать любой доступный
				hRadio = IntPtr.Zero
			};

			// Структура для данных устройства
			var deviceInfo = new BLUETOOTH_DEVICE_INFO
			{
				// Размер структуры в байтах (обязательное требование WinAPI)
				dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO))
			};

			// Начинаем перечисление устройств; возвращает дескриптор поиска
			var findHandle = BluetoothFindFirstDevice(ref searchParams, ref deviceInfo);
			// Если дескриптор == -1 (INVALID_HANDLE_VALUE) — устройств нет или ошибка
			if (findHandle == new IntPtr(-1))
				return;

			// Обрабатываем все найденные устройства
			try
			{
				// Перебираем в бесконечном цикле
				while (true)
				{
					// Проверяем, не остановили ли внешний поиск
					lock (_gate)
					{
						// Если флаг сброшен — выходим из цикла
						if (!_inquiryRunning)
							break;
					}

					// Публикуем (распространяем) текущее найденное устройство
					PublishDevice(deviceInfo);

					// Запрашиваем следующее устройство; если его нет — выходим из цикла
					if (!BluetoothFindNextDevice(findHandle, ref deviceInfo))
						break;
				}
			}
			finally
			{
				// В любом случае закрываем дескриптор поиска (освобождение ресурса)
				BluetoothFindDeviceClose(findHandle);
			}
		}
		finally
		{
			// В любом случае сбрасываем флаг опроса
			lock (_gate)
			{
				_inquiryRunning = false;
			}

			// И оповещаем подписчиков о завершении поиска
			DiscoveryFinished?.Invoke(this, EventArgs.Empty);
		}
	}

	// Публикация одного устройства подписчикам события
	private void PublishDevice(BLUETOOTH_DEVICE_INFO deviceInfo)
	{
		// Создаём нашу модель устройства
		var info = new BluetoothDeviceInfo
		{
			// Формируем МАС-адрес из 64-битного числа в привычный формат AA:BB:CC:DD:EE:FF
			Name = deviceInfo.szName,
			Address = FormatAddress(deviceInfo.Address),
			// Считаем устройство «сопряжённым», если система его помнит или подтвердила
			IsBonded = deviceInfo.fRemembered || deviceInfo.fAuthenticated
		};

		// Пропускаем «мусорные» записи, где нет ни имени, ни адреса
		if (string.IsNullOrWhiteSpace(info.Name) && string.IsNullOrWhiteSpace(info.Address))
			return;

		// Оповещаем подписчиков о найденном устройстве
		DeviceDiscovered?.Invoke(this, info);
	}

	// Преобразование 64-битного адреса в строку вида «AA:BB:CC:DD:EE:FF»
	private static string FormatAddress(ulong address)
	{
		// Форматируем число побайтово, покрывая каждый байт как шестнадцатеричный
		return string.Format(
			"{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
			// Старший байт (5-й, биты 40..47)
			(address >> 40) & 0xFF,
			// Байт 4 (биты 32..39)
			(address >> 32) & 0xFF,
			// Байт 3 (биты 24..31)
			(address >> 24) & 0xFF,
			// Байт 2 (биты 16..23)
			(address >> 16) & 0xFF,
			// Байт 1 (биты 8..15)
			(address >> 8) & 0xFF,
			// Младший байт (биты 0..7)
			address & 0xFF);
	}

	// P/Invoke: импорт функции BluetoothFindFirstDevice из библиотеки bthprops.cpl
	// (начинает перечисление Bluetooth-устройств, возвращает дескриптор поиска)
	[DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern IntPtr BluetoothFindFirstDevice(
		// Параметры поиска (передаются по ссылке)
		ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp,
		// Структура для данных первого найденного устройства
		ref BLUETOOTH_DEVICE_INFO pbtdi);

	// P/Invoke: импорт функции BluetoothFindNextDevice
	// (получает данные следующего устройства; возвращает false, если устройства кончились)
	[DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

	// P/Invoke: импорт функции BluetoothFindDeviceClose
	// (закрывает дескриптор поиска и освобождает ресурсы)
	[DllImport("bthprops.cpl", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

	// Спецификация структуры BLUETOOTH_DEVICE_SEARCH_PARAMS (должна совпадать с макетом в памяти WinAPI)
	[StructLayout(LayoutKind.Sequential)]
	private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
	{
		// Размер структуры в байтах
		public uint dwSize;
		// Флаг: возвращать авторизованные устройства
		public uint fReturnAuthenticated;
		// Флаг: возвращать запомненные устройства
		public uint fReturnRemembered;
		// Флаг: возвращать неизвестные устройства
		public uint fReturnUnknown;
		// Флаг: возвращать подключённые устройства
		public uint fReturnConnected;
		// Флаг: запускать физический опрос (inquiry)
		public uint fIssueInquiry;
		// Множитель таймаута опроса (единица ≈ 1.28 секунды)
		public byte cTimeoutMultiplier;
		// Дескриптор радио-модуля Bluetooth (IntPtr.Zero — любой)
		public IntPtr hRadio;
	}

	// Спецификация структуры BLUETOOTH_DEVICE_INFO (данные об устройстве)
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct BLUETOOTH_DEVICE_INFO
	{
		// Размер структуры в байтах
		public uint dwSize;
		// 64-битный МАС-адрес устройства
		public ulong Address;
		// Класс/код класса устройства (тип устройства)
		public uint ulClassofDevice;
		// Флаг: устройство подключено
		[MarshalAs(UnmanagedType.Bool)]
		public bool fConnected;
		// Флаг: устройство запомнено
		[MarshalAs(UnmanagedType.Bool)]
		public bool fRemembered;
		// Флаг: устройство авторизовано
		[MarshalAs(UnmanagedType.Bool)]
		public bool fAuthenticated;
		// Время последнего появления (SYSTEMTIME)
		public SYSTEMTIME stLastSeen;
		// Время последнего использования (SYSTEMTIME)
		public SYSTEMTIME stLastUsed;
		// Имя устройства — строка из 248 символов (WinAPI-массив)
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
		public string szName;
	}

	// Структура SYSTEMTIME (системная дата/время Windows)
	[StructLayout(LayoutKind.Sequential)]
	private struct SYSTEMTIME
	{
		// Год
		public ushort wYear;
		// Месяц (1–12)
		public ushort wMonth;
		// День недели (0–6)
		public ushort wDayOfWeek;
		// День месяца (1–31)
		public ushort wDay;
		// Час (0–23)
		public ushort wHour;
		// Минута (0–59)
		public ushort wMinute;
		// Секунда (0–59)
		public ushort wSecond;
		// Миллисекунды (0–999)
		public ushort wMilliseconds;
	}
}