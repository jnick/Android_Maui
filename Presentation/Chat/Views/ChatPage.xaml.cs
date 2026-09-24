// Подключаем пространство имён с уведомлениями об изменении коллекций (NotifyCollectionChangedEventArgs)
using System.Collections.Specialized;
// Подключаем пространство имён с моделью данных Bluetooth-устройства
using MauiBluethooth.Models;
// Подключаем пространство имён, где находится модель представления (ViewModel) этой страницы
using MauiBluethooth.Presentation.Chat.ViewModels;

// Объявляем пространство имён для представлений раздела «Чат»
namespace MauiBluethooth.Presentation.Chat.Views;

// Объявление частичного класса страницы чата с Bluetooth-устройством.
// IQueryAttributable — для получения параметра Device при навигации
public partial class ChatPage : ContentPage, IQueryAttributable
{
	// Ссылка на модель представления (инжектируется через конструктор)
	private readonly ChatViewModel _viewModel;

	// Конструктор страницы. ViewModel передаётся через внедрение зависимостей (DI)
	public ChatPage(ChatViewModel viewModel)
	{
		// Инициализирует компоненты, объявленные в XAML-файле ChatPage.xaml
		InitializeComponent();
		// Сохраняем ViewModel в приватное поле
		_viewModel = viewModel;
		// Привязываем ViewModel как контекст данных страницы
		BindingContext = viewModel;
	}

	// Обработчик появления страницы — подписываемся на прокрутку списка
	protected override void OnAppearing()
	{
		// Вызываем базовую реализацию
		base.OnAppearing();
		// Сначала отписываемся (защита от двойной подписки), затем подписываемся заново
		_viewModel.Messages.CollectionChanged -= OnMessagesChanged;
		// Подписываемся на изменения коллекции, чтобы автоматически прокручивать список
		_viewModel.Messages.CollectionChanged += OnMessagesChanged;
	}

	// Обработчик исчезновения страницы (навигация назад) — отключаемся от устройства
	protected override async void OnDisappearing()
	{
		// Отписываемся от прокрутки
		_viewModel.Messages.CollectionChanged -= OnMessagesChanged;
		// Вызываем базовую реализацию
		base.OnDisappearing();
		// Отписываем страницу от событий сервиса и отключаемся от устройства
		await _viewModel.OnDisappearingAsync();
	}

	// Получение параметров навигации (передача объекта Device при открытии через GoToAsync)
	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		// Ищем в параметрах ключ «Device»
		if (query.TryGetValue("Device", out var value) && value is BluetoothDeviceInfo device)
			// Передаём данные устройства в модель представления
			_viewModel.Initialize(device);
	}

	// Прокрутка к последнему сообщению при добавлении нового
	private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		// Если в списке нет сообщений — нечего прокручивать
		if (_viewModel.Messages.Count == 0)
			return;

		// Сохраняем ссылку на самое нижнее сообщение
		var lastMessage = _viewModel.Messages[^1];
		// Откладываем прокрутку в очередь диспетчера UI: к этому моменту
		// платформа уже обработает уведомление об изменении и обновит адаптер,
		// а иначе Android (RecyclerView) падает с «Invalid target position»
		Dispatcher.Dispatch(() =>
		{
			// Страховка от гонок «адаптер обновлён, но layout ещё не прошёл»
			try
			{
				// Прокручиваем список к самому нижнему сообщению с анимацией
				MessagesList.ScrollTo(lastMessage, position: ScrollToPosition.End, animate: true);
			}
			catch (Exception)
			{
				// Прокрутка некритична — пропускаем, если список ещё не готов к прокрутке
			}
		});
	}
}