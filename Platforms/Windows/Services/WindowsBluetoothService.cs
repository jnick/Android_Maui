// Подключаем пространство имён для P/Invoke-вызовов (DllImport, Marshal, StructLayout)
using System.Runtime.InteropServices;
// Подключаем средства кодирования текста (Encoding) и накопления строк (не требуется для Reader, но пригодится)
using System.Text;
// Подключаем WinRT Bluetooth API (BluetoothDevice, BluetoothCacheMode)
using Windows.Devices.Bluetooth;
// Подключаем WinRT RFCOMM API (RfcommServiceId, RfcommDeviceService)
using Windows.Devices.Bluetooth.Rfcomm;
// Подключаем WinRT сетевые сокеты (StreamSocket, SocketProtectionLevel)
using Windows.Networking.Sockets;
// Подключаем WinRT потоки данных (DataWriter)
using Windows.Storage.Streams;
// Подключаем наши модели данных (BluetoothDeviceInfo)
using MauiBluethooth.Models;
// Подключаем интерфейс Bluetooth-сервиса
using MauiBluethooth.Services;

// Объявляем пространство имён Windows-реализаций сервисов
namespace MauiBluethooth.Platforms.Windows.Services;

// Реализация IBluetoothService для платформы Windows.
// Поиск — через старый Bluetooth-стек Windows (P/Invoke из bthprops.cpl).
// Подключение/обмен данными — через RFCOMM (Windows.Devices.Bluetooth.Rfcomm).
// ВАЖНО: Windows умеет подключаться только к СОПРЯЖЁННЫМ устройствам.
public class WindowsBluetoothService : IBluetoothService
{
	// Множитель таймаута опроса устройств (по умолчанию, рекомендованный Windows)
	private const int DefaultInquiryTimeoutMultiplier = 3;

	// Объект-блокировка для потокобезопасного доступа к полю _inquiryRunning
	private readonly object _gate = new();
	// Флаг: идёт ли сейчас опрос (inquiry) устройств
	private bool _inquiryRunning;
	// Признак установленного соединения с устройством
	private bool _connected;
	// Активный RFCOMM-сокет соединения (null, если соединения нет)
	private StreamSocket? _socket;
	// Объект для отмены фонового цикла чтения данных
	private CancellationTokenSource? _readCts;

	// Признак того, что соединение с устройством установлено
	public bool IsConnected => _connected;

	// Событие: найдено новое Bluetooth-устройство
	public event EventHandler<BluetoothDeviceInfo>? DeviceDiscovered;

	// Событие: поиск завершён
	public event EventHandler? DiscoveryFinished;

	// Событие: получена строка данных от устройства
	public event EventHandler<string>? DataReceived;

	// Событие: изменилось состояние подключения
	public event EventHandler<bool>? ConnectionChanged;

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

	// Подключение к устройству по адресу (RFCOMM Serial Port)
	public async Task<bool> ConnectAsync(BluetoothDeviceInfo device)
	{
		// Если уже подключены — повторно не подключаемся
		if (_socket is not null)
			return true;

		// Преобразуем строку адреса «AA:BB:CC:DD:EE:FF» обратно в 64-битное число для WinRT
		var address = ParseAddress(device.Address);
		// Если адрес некорректный — сразу сообщаем о неудаче
		if (address == 0)
			return false;

		// Обрабатываем ошибки подключения
		try
		{
			// Получаем объект устройства Windows по его Bluetooth-адресу
			var btDevice = await BluetoothDevice.FromBluetoothAddressAsync(address);
			// Если устройство не найдено в системе — выходим
			if (btDevice is null)
				return false;

			// Ищем RFCOMM-сервис Serial Port (принудительный опрос, без кэша)
			var servicesResult = await btDevice.GetRfcommServicesForIdAsync(RfcommServiceId.SerialPort, BluetoothCacheMode.Uncached);
			// Берём первый найденный сервис
			var service = servicesResult?.Services.FirstOrDefault();
			// Если сервиса нет (устройство не сопряжено и/или не поддерживает SPP) — выходим
			if (service is null)
				return false;

			// Создаём потоковый сокет для обмена данными
			var socket = new StreamSocket();
			// Устанавливаем соединение с сервисом устройства (разрешена нулевая аутентификация — обычный SPP)
			await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName, SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

			// Сохраняем сокет и фиксируем статус подключения
			_socket = socket;
			_connected = true;
			// Оповещаем подписчиков: соединение установлено
			ConnectionChanged?.Invoke(this, true);
			// Запускаем фоновый цикл чтения входящих данных
			StartReadLoop(socket);
			// Подключение успешно
			return true;
		}
		catch (Exception ex)
		{
			// Выводим причину в отладчик для диагностики
			System.Diagnostics.Debug.WriteLine($"Bluetooth connect error: {ex}");
			// Сбрасываем флаг подключения
			_connected = false;
			// Оповещаем подписчиков: соединение не установлено
			ConnectionChanged?.Invoke(this, false);
			// Возвращаем неудачу
			return false;
		}
	}

	// Отключение от устройства
	public Task DisconnectAsync()
	{
		// Отменяем фоновый цикл чтения, если он запущен
		_readCts?.Cancel();
		// Забираем сокет и обнуляем ссылку, чтобы закрытие не помешало повторному подключению
		var socket = _socket;
		// Обнуляем поле сокета
		_socket = null;
		// Если сокет был
		if (socket is not null)
		{
			// Закрываем соединение
			try { socket.Dispose(); }
			// Игнорируем ошибки закрытия
			catch (Exception) { }
		}
		// Сбрасываем флаг подключения
		_connected = false;
		// Оповещаем подписчиков: соединение закрыто
		ConnectionChanged?.Invoke(this, false);
		// Возвращаем завершённую задачу
		return Task.CompletedTask;
	}

	// Отправка текстовой строки устройству (дописывается символ перевода строки)
	public async Task SendDataAsync(string data)
	{
		// Если соединения нет — выбрасываем исключение
		if (_socket is null)
			throw new InvalidOperationException("Нет подключения к устройству.");

		// Кодируем сообщение + «перевод строки» в байты UTF-8
		var bytes = Encoding.UTF8.GetBytes(data + "\n");
		// Создаём писатель данных для потока вывода сокета
		var writer = new DataWriter(_socket.OutputStream);
		// Записываем байты в писатель
		writer.WriteBytes(bytes);
		// Отправляем данные в эфир и дожидаемся завершения отправки
		await writer.StoreAsync();
	}

	// Фоновый метод опроса устройств (получен из discovery-части, не трогаем)
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

	// Запуск фонового цикла чтения входящих данных
	private void StartReadLoop(StreamSocket socket)
	{
		// Отменяем предыдущий цикл, если он был
		_readCts?.Cancel();
		// Создаём новый токен отмены
		_readCts = new CancellationTokenSource();
		// Запоминаем токен локально
		var token = _readCts.Token;
		// Запускаем чтение в фоновом потоке (fire-and-forget)
		_ = Task.Run(() => ReadLoopAsync(socket, token));
	}

	// Фоновый цикл чтения построчных данных из сокета
	private async Task ReadLoopAsync(StreamSocket socket, CancellationToken token)
	{
		// Оборачиваем входящий поток сокета в StreamReader с кодировкой UTF-8
		var reader = new StreamReader(socket.InputStream.AsStreamForRead(), Encoding.UTF8);
		// Обрабатываем возможные ошибки и отмену
		try
		{
			// Читаем, пока не запросили отмену
			while (!token.IsCancellationRequested)
			{
				// Читаем очередную строку (до '\n') из потока
				var line = await reader.ReadLineAsync(token);
				// Если поток закрыт (null) — выходим из цикла
				if (line is null)
					break;
				// Пропускаем пустые строки
				if (line.Length > 0)
					// Передаём строку подписчикам
					DataReceived?.Invoke(this, line);
			}
		}
		catch (OperationCanceledException)
		{
			// Чтение отменено при отключении — нормальная ситуация
		}
		catch (Exception)
		{
			// Ошибка чтения = потеря соединения (устройство отключилось)
		}
		finally
		{
			// Сбрасываем флаг подключения
			_connected = false;
			// Оповещаем подписчиков: соединение потеряно/закрыто
			ConnectionChanged?.Invoke(this, false);
		}
	}

	// Публикация одного устройства подписчикам события
	private void PublishDevice(BLUETOOTH_DEVICE_INFO deviceInfo)
	{
		// Создаём нашу модель устройства
		var info = new BluetoothDeviceInfo
		{
			// Имя устройства из структуры WINAPI
			Name = deviceInfo.szName,
			// Формируем МАС-адрес из 64-битного числа в привычный формат AA:BB:CC:DD:EE:FF
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

	// Обратное преобразование строки «AA:BB:CC:DD:EE:FF» в 64-битный адрес
	private static ulong ParseAddress(string address)
	{
		// Разбиваем адрес на шесть байтовых групп
		var parts = address.Split(':');
		// Если формат не тот (не 6 групп) — возвращаем 0 (невалидный адрес)
		if (parts.Length != 6)
			return 0;

		// Переменная для накопления результата
		ulong value = 0;
		// Проходим по всем группам адреса
		foreach (var part in parts)
		{
			// Пытаемся разобрать группу как шестнадцатеричный байт
			if (!byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var b))
				// Если разбор не удался — адрес невалиден
				return 0;
			// Учитываем байт в результирующем адресе (сдвиг на 8 бит)
			value = (value << 8) | b;
		}
		// Возвращаем адрес
		return value;
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