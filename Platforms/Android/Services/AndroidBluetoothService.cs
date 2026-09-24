// Подключаем пространство имён Android с API Bluetooth (BluetoothAdapter, BluetoothDevice и др.)
using Android.Bluetooth;
// Подключаем пространство имён Android с контекстом и интентами (Context, IntentFilter, BroadcastReceiver)
using Android.Content;
// Подключаем наши модели данных (BluetoothDeviceInfo)
using MauiBluethooth.Models;
// Подключаем интерфейс Bluetooth-сервиса
using MauiBluethooth.Services;

// ОТКЛЮЧАЕМ предупреждение CA1422: BluetoothAdapter.DefaultAdapter и Intent.GetParcelableExtra 
// помечены obsolete только на новых уровнях API, но код должен работать и на старых
#pragma warning disable CA1422 // BluetoothAdapter.DefaultAdapter and Intent.GetParcelableExtra marked obsolete on newer API levels only

// Объявляем пространство имён Android-реализаций сервисов
namespace MauiBluethooth.Platforms.Android.Services;

// Реализация IBluetoothService для платформы Android
public class AndroidBluetoothService : IBluetoothService
{
	// Контекст приложения Android (нужен для регистрации приёмника широковещательных сообщений)
	private readonly Context _context = global::Android.App.Application.Context;
	// Адаптер Bluetooth — системный объект для управления Bluetooth; null, если Bluetooth недоступен
	private readonly BluetoothAdapter? _adapter = BluetoothAdapter.DefaultAdapter;
	// Приёмник широковещательных сообщений о найденных устройствах (инициализируется при запуске поиска)
	private DiscoveryReceiver? _receiver;

	// Событие: найдено новое Bluetooth-устройство
	public event EventHandler<BluetoothDeviceInfo>? DeviceDiscovered;

	// Событие: поиск завершён
	public event EventHandler? DiscoveryFinished;

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
			_onFound = onFound;
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