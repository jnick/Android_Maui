// Подключаем пространство имён с коллекцией ObservableCollection (автообновляемая коллекция для привязок)
using System.Collections.ObjectModel;
// Подключаем базовые классы MVVM (ObservableObject, атрибут ObservableProperty)
using CommunityToolkit.Mvvm.ComponentModel;
// Подключаем компоненты для RelayCommand (команды, вызываемые из XAML)
using CommunityToolkit.Mvvm.Input;
// Подключаем наши модели данных (BluetoothDeviceInfo, ChatMessage)
using MauiBluethooth.Models;
// Подключаем интерфейс Bluetooth-сервиса
using MauiBluethooth.Services;
// Подключаем передатчик распознанных объектов по Bluetooth
using MauiBluethooth.Services.Detection.Transmission;

// Объявляем пространство имён для моделей представления раздела «Чат»
namespace MauiBluethooth.Presentation.Chat.ViewModels;

// Модель представления экрана чата с Bluetooth-устройством
public partial class ChatViewModel : ObservableObject
{
	// Ссылка на Bluetooth-сервис (инжектируется через конструктор)
	private readonly IBluetoothService _bluetoothService;

	// Ссылка на передатчик распознанных объектов.
	// Он испускает событие PacketSent для каждой строки, уходящей устройству, —
	// эти строки чат показывает как свои исходящие сообщения
	private readonly DetectionTransmitter _transmitter;

	// Флаг: подписаны ли мы на события сервиса (чтобы не подписываться повторно)
	private bool _isSubscribed;

	// Максимальное число сообщений в чате.
	// Ограничение важно: передатчик шлёт строки 5 раз в секунду, и без лимита
	// список рос бы бесконечно, выжирая память. При достижении лимита
	// самые старые сообщения удаляются (см. TrimMessages)
	private const int MaxMessages = 100;

	// Коллекция сообщений чата для отображения в списке
	public ObservableCollection<ChatMessage> Messages { get; } = [];

	// [ObservableProperty] генерирует публичное свойство DeviceName (имя устройства в заголовке)
	[ObservableProperty]
	// Поле, хранящее имя подключённого устройства
	private string deviceName = string.Empty;

	// [ObservableProperty] генерирует публичное свойство ConnectionStatus (текст статуса соединения)
	[ObservableProperty]
	// Поле, хранящее строку статуса соединения
	private string connectionStatus = "Подключение...";

	// [ObservableProperty] генерирует публичное свойство InputText (текст поля ввода)
	[ObservableProperty]
	// Поле, хранящее текст, вводимый пользователем
	private string inputText = string.Empty;

	// [ObservableProperty] генерирует публичное свойство IsConnected (состояние соединения)
	[ObservableProperty]
	// Флаг: установлено ли соединение с устройством
	private bool isConnected;

	// Конструктор: получает Bluetooth-сервис и передатчик из DI
	public ChatViewModel(IBluetoothService bluetoothService, DetectionTransmitter transmitter)
	{
		// Сохраняем сервис в приватное поле
		_bluetoothService = bluetoothService;

		// Сохраняем передатчик
		_transmitter = transmitter;

		// Подписываемся на отправленные пакеты ПРОСТО ТАК, в конструкторе, а не в Subscribe().
		// Зачем: пакеты распознавания уходят, пока пользователь на экране камеры,
		// а чат при этом не открыт. Если подписаться только при открытии чата,
		// эти строки были бы потеряны. Подписка в конструкторе (View‑модель — синглтон)
		// означает: чат собирает историю передач всегда, и открывшийся чат её показывает
		_transmitter.PacketSent += OnPacketSent;
	}

	// Инициализация экрана данными выбранного устройства (вызывается при открытии страницы)
	public void Initialize(BluetoothDeviceInfo device)
	{
		// Показываем имя устройства в заголовке (пустое имя заменяем на нейтральное)
		DeviceName = string.IsNullOrWhiteSpace(device.Name) ? "Неизвестное устройство" : device.Name;
		// Считываем текущее состояние соединения из сервиса
		IsConnected = _bluetoothService.IsConnected;
		// Отображаем текст статуса по состоянию соединения
		ConnectionStatus = IsConnected ? "Подключено" : "Нет соединения";
		// Подписываемся на события сервиса (если ещё не подписаны)
		Subscribe();
		// Если соединение уже установлено — добавляем системное приветственное сообщение
		if (IsConnected)
			// Сообщаем пользователю об установленном соединении
			AddSystemMessage("Соединение установлено.");
	}

	// Команда отправки сообщения устройству
	[RelayCommand]
	// Метод команды «Отправить»
	private async Task SendAsync()
	{
		// Сохраняем введённый текст
		var message = InputText;
		// Пустые сообщения не отправляем
		if (string.IsNullOrWhiteSpace(message))
			return;

		// Если соединение потеряно — предупреждаем и не отправляем
		if (!IsConnected)
		{
			// Добавляем системное сообщение об отсутствии соединения
			AddSystemMessage("Нет соединения с устройством.");
			return;
		}

		// Очищаем поле ввода (сообщение уже сохранено в локальную переменную)
		InputText = string.Empty;
		// Добавляем исходящее сообщение в список чата
		Messages.Add(new ChatMessage { Text = message, IsIncoming = false });

		// Обрабатываем ошибки отправки
		try
		{
			// Отправляем текст устройству через сервис
			await _bluetoothService.SendDataAsync(message);
		}
		catch (Exception ex)
		{
			// Добавляем системное сообщение с текстом ошибки
			AddSystemMessage($"Ошибка отправки: {ex.Message}");
		}
	}

	// Команда отключения от устройства
	[RelayCommand]
	// Метод команды «Отключиться»
	private async Task DisconnectAsync()
	{
		// Останавливаем соединение с устройством
		await Disconnect();
		// Возвращаемся на предыдущий экран (список устройств)
		await Shell.Current.GoToAsync("..");
	}

	// Вызывается при исчезновении страницы (навигация назад) — отписываемся и отключаемся
	public async Task OnDisappearingAsync()
	{
		// Отписываемся от событий сервиса
		Unsubscribe();
		// Отключаемся от устройства, если соединение было установлено
		await Disconnect();
	}

	// Подписка на события сервиса
	private void Subscribe()
	{
		// Если уже подписаны — выходим (защита от повторной подписки)
		if (_isSubscribed)
			return;

		// Подписываемся на событие получения данных от устройства
		_bluetoothService.DataReceived += OnDataReceived;
		// Подписываемся на событие изменения состояния соединения
		_bluetoothService.ConnectionChanged += OnConnectionChanged;
		// Отмечаем, что подписаны
		_isSubscribed = true;
	}

	// Отписка от событий сервиса
	private void Unsubscribe()
	{
		// Если не подписаны — выходим
		if (!_isSubscribed)
			return;

		// Отписываемся от события получения данных
		_bluetoothService.DataReceived -= OnDataReceived;
		// Отписываемся от события изменения состояния соединения
		_bluetoothService.ConnectionChanged -= OnConnectionChanged;
		// Отмечаем, что отписаны
		_isSubscribed = false;
	}

	// Отключение от устройства с обработкой ошибок
	private async Task Disconnect()
	{
		// Если соединения нет — ничего не делаем
		if (!_bluetoothService.IsConnected)
			return;

		// Обрабатываем ошибки отключения
		try
		{
			// Вызываем отключение через сервис
			await _bluetoothService.DisconnectAsync();
		}
		catch (Exception ex)
		{
			// Добавляем системное сообщение с текстом ошибки
			AddSystemMessage($"Ошибка отключения: {ex.Message}");
		}
	}

	// Обработчик получения строки данных от устройства
	private void OnDataReceived(object? sender, string data)
	{
		// Данные приходят из фонового потока — обновляем UI в главном потоке
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Добавляем входящее сообщение в список чата
			Messages.Add(new ChatMessage { Text = data, IsIncoming = true });
		});
	}

	// Обработчик события передатчика «строка-пакет отправлена устройству».
	// ВАЖНО: событие приходит с потока таймера передатчика, а не из UI-потока
	private void OnPacketSent(object? sender, string line)
	{
		// Переносим изменение коллекции в UI-поток:
		// ObservableCollection нельзя менять из фонового потока
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Добавляем отправленную строку как ИСХОДЯЩЕЕ сообщение (синий пузырь справа) —
			// ровно как обычное сообщение, которое пользователь отправил кнопкой.
			// Так видно, что именно уходит в эфир во время передачи распознавания
			Messages.Add(new ChatMessage { Text = line, IsIncoming = false });

			// Удерживаем список в пределах лимита: удаляем самые старые строки
			TrimMessages();
		});
	}

	// Обрезает список сообщений до MaxMessages, удаляя самые старые.
	// Сообщения добавляются в конец, значит самые старые — в начале (индекс 0).
	// RemoveAt(0) сдвигает все элементы влево, зато список никогда не разбухает
	private void TrimMessages()
	{
		// Пока сообщений больше лимита — удаляем по одному старому из начала
		while (Messages.Count > MaxMessages)
		{
			// Удаляем самое старое сообщение
			Messages.RemoveAt(0);
		}
	}

	// Обработчик изменения состояния соединения
	private void OnConnectionChanged(object? sender, bool connected)
	{
		// Событие приходит из фонового потока — обновляем UI в главном потоке
		MainThread.BeginInvokeOnMainThread(() =>
		{
			// Обновляем флаг соединения
			IsConnected = connected;
			// Обновляем текст статуса
			ConnectionStatus = connected ? "Подключено" : "Нет соединения";
			// Если соединение неожиданно потеряно — предупреждаем пользователя
			if (!connected)
				AddSystemMessage("Соединение потеряно.");
		});
	}

	// Добавление системного сообщения (серого, по центру)
	private void AddSystemMessage(string text)
	{
		// Создаём и добавляем сообщение с флагом IsSystem
		Messages.Add(new ChatMessage { Text = text, IsSystem = true });
	}
}