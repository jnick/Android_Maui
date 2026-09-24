// Подключаем пространство имён с коллекцией ObservableCollection (автообновляемая коллекция для привязок)
using System.Collections.ObjectModel;
// Подключаем базовые классы MVVM (ObservableObject, атрибут ObservableProperty)
using CommunityToolkit.Mvvm.ComponentModel;
// Подключаем компоненты для RelayCommand (команды, вызываемые из XAML)
using CommunityToolkit.Mvvm.Input;
// Подключаем модели данных приложения
using MauiBluethooth.Models;
// Подключаем интерфейс Bluetooth-сервиса
using MauiBluethooth.Services;

// Объявляем пространство имён для моделей представления раздела «Устройства»
namespace MauiBluethooth.Presentation.Devices.ViewModels;

// Модель представления экрана со списком устройств.
// IDisposable — чтобы отписываться от событий сервиса при уничтожении экрана
public partial class DevicesViewModel : ObservableObject, IDisposable
{
	// Ссылка на Bluetooth-сервис (инжектируется через конструктор)
	private readonly IBluetoothService _bluetoothService;

	// Конструктор: получает Bluetooth-сервис и подписывается на его события
	public DevicesViewModel(IBluetoothService bluetoothService)
	{
		// Сохраняем сервис в приватное поле
		_bluetoothService = bluetoothService;
		// Подписываемся на событие нахождения устройства
		_bluetoothService.DeviceDiscovered += OnDeviceDiscovered;
		// Подписываемся на событие завершения поиска
		_bluetoothService.DiscoveryFinished += OnDiscoveryFinished;
	}

	// Коллекция найденных устройств. «[]» — сокращённая инициализация пустой коллекции
	public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];

	// [ObservableProperty] генерирует свойство IsSearching (идёт ли сейчас поиск)
	[ObservableProperty]
	// [NotifyPropertyChangedFor] — при изменении IsSearching уведомить UI об изменении SearchButtonText
	[NotifyPropertyChangedFor(nameof(SearchButtonText))]
	// Флаг: выполняется ли поиск устройств в данный момент
	private bool isSearching;

	// [ObservableProperty] генерирует свойство SelectedDevice (выбранное устройство в списке)
	[ObservableProperty]
	// [NotifyCanExecuteChangedFor] — при изменении выбранного устройства пересчитать доступность ConnectCommand
	[NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
	// Выбранное пользователем устройство в списке (может быть null, если ничего не выбрано)
	private DeviceItemViewModel? selectedDevice;

	// [ObservableProperty] генерирует свойство StatusMessage (строка статуса/уведомлений в UI)
	[ObservableProperty]
	// Поле, хранящее текущее сообщение о состоянии приложения
	private string statusMessage = string.Empty;

	// Текст кнопки поиска: «Стоп», если идёт поиск, иначе «Поиск»
	public string SearchButtonText => IsSearching ? "Стоп" : "Поиск";

	// Команда подключения к выбранному устройству. CanExecute из метода CanConnect
	[RelayCommand(CanExecute = nameof(CanConnect))]
	// Метод команды: обрабатывает нажатие кнопки «Подключиться»
	private async Task Connect()
	{
		// Если устройство не выбрано — выходим (защита от ошибок)
		if (SelectedDevice is null)
			return;

		// Если в этот момент идёт поиск — останавливаем его (во время поиска соединение не установить)
		if (IsSearching)
			await StopSearchAsync();

		// Обрабатываем ошибки самого подключения
		try
		{
			// Вызываем подключение к выбранному устройству через сервис
			var ok = await _bluetoothService.ConnectAsync(SelectedDevice.Device);
			// Если подключение не удалось
			if (!ok)
			{
				// Сообщаем пользователю, что подключение не состоялось
				StatusMessage = "Не удалось подключиться. Убедитесь, что устройство включено и доступно.";
				return;
			}
		}
		catch (Exception ex)
		{
			// Показываем текст ошибки подключения
			StatusMessage = $"Ошибка подключения: {ex.Message}";
			return;
		}

		// Подключение успешно — открываем экран чата, передавая ему выбранное устройство
		StatusMessage = "Подключено. Открываю чат...";
		// Используем Shell-навигацию по маршруту «chat» с параметром Device
		await Shell.Current.GoToAsync("chat", new Dictionary<string, object>
		{
			// Передаём данные устройства в режим чата
			["Device"] = SelectedDevice.Device
		});
	}

	// Проверка, можно ли выполнять команду Connect (только если выбранное устройство существует)
	private bool CanConnect() => SelectedDevice is not null;

	// Команда переключения поиска (одна кнопка: «Поиск»/«Стоп»)
	[RelayCommand]
	// Метод команды — асинхронный, вызывается при нажатии кнопки поиска
	private async Task ToggleSearch()
	{
		// Если поиск уже идёт — значит пользователь нажал «Стоп»
		if (IsSearching)
		{
			// Останавливаем поиск
			await StopSearchAsync();
			return;
		}

		// Запрашиваем разрешения на доступ к Bluetooth
		if (!await _bluetoothService.RequestPermissionsAsync())
		{
			// Если разрешений нет — сообщаем пользователю, что нужно их включить
			StatusMessage = "Нет доступа к Bluetooth. Проверьте разрешения в настройках.";
			return;
		}

		// Очищаем список найденных устройств перед новым поиском
		Devices.Clear();
		// Сообщаем пользователю о начале поиска
		StatusMessage = "Идёт поиск устройств...";
		// Устанавливаем флаг поиска (кнопка меняется на «Стоп»)
		IsSearching = true;

		// Оборачиваем в try/catch, чтобы поймать ошибки при запуске поиска
		try
		{
			// Запускаем поиск устройств через сервис
			var started = await _bluetoothService.StartDiscoveryAsync();
			// Если не удалось запустить поиск
			if (!started)
			{
				// Сообщаем, что поиск не запустился (скорее всего выключен Bluetooth)
				StatusMessage = "Не удалось запустить поиск. Убедитесь, что Bluetooth включён.";
				// Снимаем флаг поиска, кнопка снова становится «Поиск»
				IsSearching = false;
			}
		}
		catch (Exception ex) // Ловим любые исключения при запуске
		{
			// Показываем текст ошибки пользователю
			StatusMessage = $"Ошибка поиска: {ex.Message}";
			// Снимаем флаг поиска
			IsSearching = false;
		}
	}

	// Внутренний метод остановки поиска
	private async Task StopSearchAsync()
	{
		// Обрабатываем ошибки остановки
		try
		{
			// Вызываем остановку поиска через сервис
			await _bluetoothService.StopDiscoveryAsync();
		}
		catch (Exception ex) // Ловим исключения остановки
		{
			// Показываем текст ошибки пользователю
			StatusMessage = $"Ошибка остановки: {ex.Message}";
		}
	}

	// Обработчик события «найдено устройство»
	private void OnDeviceDiscovered(object? sender, BluetoothDeviceInfo device)
	{
		// Метод нахождения устройства приходит из фонового потока, поэтому 
		// переносим обновление UI в главный поток
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Ищем уже существующую запись с таким же адресом в списке
			var existing = Devices.FirstOrDefault(d => d.Address == device.Address);
			// Если устройства ещё нет в списке
			if (existing is null)
			{
				// Добавляем новую запись в список
				Devices.Add(new DeviceItemViewModel(device));
			}
			else
			{
				// Устройство уже было — обновляем его данные
				existing.Update(device);
			}
		});
	}

	// Обработчик события «поиск завершён»
	private void OnDiscoveryFinished(object? sender, EventArgs e)
	{
		// Переносим обновление UI в главный поток
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Снимаем флаг поиска
			IsSearching = false;
			// Если на экране был текст «Идёт поиск» — заменяем его на «Поиск завершён»
			if (StatusMessage == "Идёт поиск устройств...")
				StatusMessage = "Поиск завершён.";
		});
	}

	// Реализация IDisposable: отписываемся от событий сервиса, чтобы избежать утечек памяти
	public void Dispose()
	{
		// Отписываемся от события нахождения устройства
		_bluetoothService.DeviceDiscovered -= OnDeviceDiscovered;
		// Отписываемся от события завершения поиска
		_bluetoothService.DiscoveryFinished -= OnDiscoveryFinished;
	}
}