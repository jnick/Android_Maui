// Подключаем пространство имён Android с API Bluetooth (BluetoothAdapter, BluetoothDevice, BluetoothSocket)
using Android.Bluetooth;
// Подключаем пространство имён Android с контекстом и интентами (Context, IntentFilter, BroadcastReceiver)
using Android.Content;
// Подключаем пространство имён Java для работы с UUID
using Java.Util;
// Подключаем наши модели данных (BluetoothDeviceInfo)
using MauiBluethooth.Models;
// Подключаем интерфейс Bluetooth-сервиса
using MauiBluethooth.Services;
// Подключаем средства кодирования текста (Encoding, StringBuilder)
using System.Text;

// ОТКЛЮЧАЕМ предупреждение CA1422: BluetoothAdapter.DefaultAdapter и Intent.GetParcelableExtra 
// помечены obsolete только на новых уровнях API, но код должен работать и на старых
#pragma warning disable CA1422 // BluetoothAdapter.DefaultAdapter and Intent.GetParcelableExtra marked obsolete on newer API levels only

// Объявляем пространство имён Android-реализаций сервисов
namespace MauiBluethooth.Platforms.Android.Services;

// Реализация IBluetoothService для платформы Android
public class AndroidBluetoothService : IBluetoothService
{
	// Стандартный UUID профиля SPP (Serial Port Profile) — именно с ним работают HC-05/HC-06.
	// «!» — подавляем nullable-предупреждение: FromString возвращает UUID? но константа всегда валидна
	private static readonly UUID SppUuid = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

	// Контекст приложения Android (нужен для регистрации приёмника широковещательных сообщений)
	private readonly Context _context = global::Android.App.Application.Context;
	// Адаптер Bluetooth — системный объект для управления Bluetooth; null, если Bluetooth недоступен
	private readonly BluetoothAdapter? _adapter = BluetoothAdapter.DefaultAdapter;
	// Приёмник широковещательных сообщений о найденных устройствах (инициализируется при запуске поиска)
	private DiscoveryReceiver? _receiver;
	// Активный Bluetooth-сокет соединения с устройством (null, если соединения нет)
	private BluetoothSocket? _socket;
	// Объект для отмены фонового цикла чтения данных
	private CancellationTokenSource? _readCts;

	// Признак того, что соединение с устройством установлено
	public bool IsConnected => _socket?.IsConnected == true;

	// Событие: найдено новое Bluetooth-устройство
	public event EventHandler<BluetoothDeviceInfo>? DeviceDiscovered;

	// Событие: поиск завершён
	public event EventHandler? DiscoveryFinished;

	// Событие: получена строка данных от устройства
	public event EventHandler<string>? DataReceived;

	// Событие: изменилось состояние подключения
	public event EventHandler<bool>? ConnectionChanged;

	// Запрос необходимых разрешений (асинхронная операция)
	public async Task<bool> RequestPermissionsAsync()
	{
		// Если версия Android меньше 31 — Beacon/поиск требует разрешение на геолокацию
		if (!OperatingSystem.IsAndroidVersionAtLeast(31))
		{
			// Проверяем статус разрешения на определение местоположения
			var locationStatus = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
			// Если разрешения ещё нет
			if (locationStatus != PermissionStatus.Granted)
				// Запрашиваем его у пользователя
				locationStatus = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
			// Если пользователь так и не дал разрешение — поиск невозможен
			if (locationStatus != PermissionStatus.Granted)
				return false;
		}

		// Проверяем статус разрешения на Bluetooth
		var bluetoothStatus = await Permissions.CheckStatusAsync<Permissions.Bluetooth>();
		// Если разрешение ещё не выдано
		if (bluetoothStatus != PermissionStatus.Granted)
			// Запрашиваем его
			bluetoothStatus = await Permissions.RequestAsync<Permissions.Bluetooth>();

		// Возвращаем true, только если разрешение на Bluetooth выдано
		return bluetoothStatus == PermissionStatus.Granted;
	}

	// Запуск поиска Bluetooth-устройств (возвращает true при успешном старте)
	public Task<bool> StartDiscoveryAsync()
	{
		// Если адаптер отсутствует или Bluetooth выключен — запустить поиск нельзя
		if (_adapter is null || !_adapter.IsEnabled)
			return Task.FromResult(false);

		// Создаём фильтр интента для приёма широковещательных сообщений
		var filter = new IntentFilter();
		// Подписываемся на сообщение «найдено устройство»
		filter.AddAction(BluetoothDevice.ActionFound);
		// Подписываемся на сообщение «поиск завершён»
		filter.AddAction(BluetoothAdapter.ActionDiscoveryFinished);

		// Создаём экземпляр приёмника, передавая ему наши обработчики
		_receiver = new DiscoveryReceiver(OnDeviceFound, OnDiscoveryFinished);

		// На Android 13+ (API 33) регистрация приёмника требует флаг Exported
		if (OperatingSystem.IsAndroidVersionAtLeast(33))
			// Регистрируем приёмник с флагом Exported (нужно для системных broadcast)
			_context.RegisterReceiver(_receiver, filter, ReceiverFlags.Exported);
		else
			// На старых Android регистрируем обычным способом
			_context.RegisterReceiver(_receiver, filter);

		// Если уже идёт какой-то поиск
		if (_adapter.IsDiscovering)
			// Отменяем предыдущий поиск
			_adapter.CancelDiscovery();

		// Пытаемся запустить поиск устройств
		if (!_adapter.StartDiscovery())
		{
			// Если запуск не удался — отменяем регистрацию приёмника
			UnregisterReceiver();
			// Сообщаем о неудаче
			return Task.FromResult(false);
		}

		// Поиск успешно запущен
		return Task.FromResult(true);
	}

	// Остановка поиска (асинхронная операция)
	public Task StopDiscoveryAsync()
	{
		// Если адаптер существует и поиск ещё идёт
		if (_adapter is not null && _adapter.IsDiscovering)
			// Отменяем поиск
			_adapter.CancelDiscovery();

		// Отписываем приёмник
		UnregisterReceiver();
		// Оповещаем подписчиков, что поиск завершён
		DiscoveryFinished?.Invoke(this, EventArgs.Empty);
		// Возвращаем завершённую задачу
		return Task.CompletedTask;
	}

	// Подключение к устройству по адресу (SPP/RFCOMM)
	public Task<bool> ConnectAsync(BluetoothDeviceInfo device)
	{
		// Если адаптера нет или Bluetooth выключен — подключение невозможно
		if (_adapter is null || !_adapter.IsEnabled)
			return Task.FromResult(false);

		// Если уже подключены — считаем операцию успешной (повторное подключение не нужно)
		if (_socket?.IsConnected == true)
			return Task.FromResult(true);

		// Во время поиска соединение устанавливать нельзя — сначала останавливаем поиск
		if (_adapter.IsDiscovering)
			_adapter.CancelDiscovery();

		// Получаем удалённое устройство по MAC-адресу
		// null! — при некорректном адресе метод бросает исключение, а не возвращает null
		BluetoothDevice remote = null!;
		// Обрабатываем ошибку получения устройства
		try
		{
			// Конвертируем строку адреса в объект BluetoothDevice («!» — подавляем nullable-предупреждение)
			remote = _adapter.GetRemoteDevice(device.Address)!;
		}
		catch (Exception)
		{
			// Адрес оказался некорректным — сообщаем о неудаче
			return Task.FromResult(false);
		}

		// Блокирующая операция Connect выполняется в фоновом потоке, чтобы не завесить UI
		return Task.Run(() =>
		{
			// Локальная переменная для попытки подключения
			BluetoothSocket? newSocket = null;
			// Обрабатываем ошибки подключения
			try
			{
				// Создаём RFCOMM-сокет для сервиса SPP («!» — подавляем nullable-предупреждение)
				newSocket = remote.CreateRfcommSocketToServiceRecord(SppUuid)!;
				// Устанавливаем соединение (блокирующий вызов; «!» — сокет точно создан выше)
				newSocket!.Connect();
				// Сохраняем сокет в поле класса
				_socket = newSocket;
				// Запускаем фоновый цикл чтения входящих данных
				StartReadLoop(newSocket);
				// Оповещаем подписчиков: соединение установлено
				ConnectionChanged?.Invoke(this, true);
				// Подключение успешно
				return true;
			}
			catch (Exception)
			{
				// При ошибке аккуратно закрываем сокет, если он создавался
				if (newSocket is not null)
				{
					// Пытаемся закрыть сокет
					try { newSocket.Close(); }
					// Игнорируем ошибки закрытия
					catch (Exception) { }
				}
				// Оповещаем подписчиков: соединение не установлено
				ConnectionChanged?.Invoke(this, false);
				// Возвращаем неудачу
				return false;
			}
		});
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
			try { socket.Close(); }
			// Игнорируем ошибки закрытия
			catch (Exception) { }
		}
		// Оповещаем подписчиков: соединение закрыто
		ConnectionChanged?.Invoke(this, false);
		// Возвращаем завершённую задачу
		return Task.CompletedTask;
	}

	// Отправка текстовой строки устройству (дописывается символ перевода строки)
	public Task SendDataAsync(string data)
	{
		// Получаем текущий сокет
		var socket = _socket;
		// Если соединения нет — выбрасываем исключение
		if (socket?.IsConnected != true)
			return Task.FromException(new InvalidOperationException("Нет подключения к устройству."));

		// Кодируем сообщение + символ «перевод строки» в байты UTF-8 (как ждут Arduino-скетчи)
		var bytes = Encoding.UTF8.GetBytes(data + "\n");
		// Обрабатываем ошибки записи
		try
		{
			// Получаем поток вывода сокета («!» — соединение проверено выше, поток не может быть null)
			var outputStream = socket!.OutputStream!;
			// Записываем байты в поток вывода сокета
			outputStream.Write(bytes, 0, bytes.Length);
			// Сбрасываем буфер потока, чтобы данные гарантированно ушли в эфир
			outputStream.Flush();
			// Отправка завершена успешно
			return Task.CompletedTask;
		}
		catch (Exception ex)
		{
			// Возвращаем задачу с ошибкой
			return Task.FromException(ex);
		}
	}

	// Запуск фонового цикла чтения входящих данных
	private void StartReadLoop(BluetoothSocket socket)
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

	// Фоновый цикл чтения данных из сокета
	private async Task ReadLoopAsync(BluetoothSocket socket, CancellationToken token)
	{
		// Получаем входящий поток сокета — источник данных устройства («!» — поток не может быть null)
		var inputStream = socket.InputStream!;
		// Буфер для накопления незавершённых строк (символы до '\n')
		var sb = new StringBuilder();
		// Буфер для чтения (1 КБ за раз)
		var buffer = new byte[1024];
		// Обрабатываем возможные ошибки и отмену
		try
		{
			// Читаем, пока не запросили отмену
			while (!token.IsCancellationRequested)
			{
				// Асинхронно читаем порцию байтов из потока
				var bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, token);
				// Если поток закрыт (0 байт) — выходим из цикла
				if (bytesRead <= 0)
					break;
				// Декодируем прочитанные байты в текст
				sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
				// Извлекаем из буфера готовые строки, разделённые '\n'
				ProcessBuffer(sb);
			}
		}
		catch (OperationCanceledException)
		{
			// Чтение отменено при отключении — это нормальная ситуация
		}
		catch (Exception)
		{
			// Ошибка чтения = потеря соединения (устройство отключилось)
		}
		finally
		{
			// Закрываем сокет, чтобы освободить системные ресурсы
			CloseSocket(socket);
			// Оповещаем подписчиков: соединение потеряно/закрыто
			ConnectionChanged?.Invoke(this, false);
		}
	}

	// Разбор буфера на строки (по символу перевода строки)
	private void ProcessBuffer(StringBuilder sb)
	{
		// Снимаем текущее содержимое буфера
		var content = sb.ToString();
		// Очищаем буфер для дальнейшего накопления
		sb.Clear();
		// Разбиваем на строки по '\n'
		var lines = content.Split('\n');
		// Обрабатываем все полные строки (кроме последней — хвост ещё не завершён)
		for (var i = 0; i < lines.Length - 1; i++)
		{
			// Убираем символ возврата каретки '\r', добавленный старыми устройствами
			var line = lines[i].TrimEnd('\r');
			// Пустые строки пропускаем
			if (line.Length > 0)
				// Передаём строку подписчикам
				DataReceived?.Invoke(this, line);
		}
		// Неполный хвост возвращаем в буфер для следующей порции данных
		sb.Append(lines[^1]);
	}

	// Закрытие сокета с игнорированием ошибок
	private static void CloseSocket(BluetoothSocket socket)
	{
		// Пытаемся закрыть сокет
		try { socket.Close(); }
		// Игнорируем ошибки закрытия (сокет мог быть уже закрыт)
		catch (Exception) { }
	}

	// Обработчик: найдено устройство (вызывается приёмником из системного потока)
	private void OnDeviceFound(BluetoothDevice device)
	{
		// Создаём нашу модель с данными об устройстве
		var info = new BluetoothDeviceInfo
		{
			// Берём имя устройства (пустая строка, если имя отсутствует)
			Name = device.Name ?? string.Empty,
			// Берём МАС-адрес устройства
			Address = device.Address ?? string.Empty,
			// Определяем, сопряжено ли устройство (BondState == Bonded)
			IsBonded = device.BondState == Bond.Bonded
		};

		// Оповещаем подписчиков о найденном устройстве
		DeviceDiscovered?.Invoke(this, info);
	}

	// Обработчик: поиск завершён (система сама сообщила, что поиск окончен)
	private void OnDiscoveryFinished()
	{
		// Отписываем приёмник
		TopLevelUnregisterReceiver();
		// Оповещаем подписчиков о завершении поиска
		DiscoveryFinished?.Invoke(this, EventArgs.Empty);
	}

	// Обёртка для единообразного отписывания приёмника
	private void UnregisterReceiver() => TopLevelUnregisterReceiver();

	// Основная логика отписывания приёмника
	private void TopLevelUnregisterReceiver()
	{
		// Если приёмник не был зарегистрирован — ничего не делаем
		if (_receiver is null)
			return;

		// Обрабатываем возможную ошибку отписывания
		try
		{
			// Отписываем приёмник от контекста
			_context.UnregisterReceiver(_receiver);
		}
		catch (ArgumentException)
		{
			// Приёмник мог быть уже отписан — игнорируем исключение
		}

		// Обнуляем ссылку на приёмник
		_receiver = null;
	}

	// Внутренний класс-приёмник широковещательных сообщений Android
	private sealed class DiscoveryReceiver : BroadcastReceiver
	{
		// Делегат, вызываемый при нахождении устройства
		private readonly Action<BluetoothDevice> _onFound;
		// Делегат, вызываемый при завершении поиска
		private readonly Action _onFinished;

		// Конструктор приёмника: сохраняем переданные обработчики
		public DiscoveryReceiver(Action<BluetoothDevice> onFound, Action onFinished)
		{
			// Сохраняем обработчик найденного устройства
			_onFound = onFound;
			// Сохраняем обработчик завершения поиска
			_onFinished = onFinished;
		}

		// Метод Android, вызываемый при получении широковещательного сообщения
		public override void OnReceive(Context? context, Intent? intent)
		{
			// Если это сообщение «найдено устройство»
			if (intent?.Action == BluetoothDevice.ActionFound)
			{
				// Извлекаем объект устройства из дополнительных данных интента
				var device = (BluetoothDevice?)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
				// Если устройство извлечено
				if (device is not null)
					// Передаём его в обработчик «найденного» устройства
					_onFound(device);
			}
			// Иначе, если это сообщение «поиск завершён»
			else if (intent?.Action == BluetoothAdapter.ActionDiscoveryFinished)
			{
				// Вызываем обработчик завершения поиска
				_onFinished();
			}
		}
	}
}