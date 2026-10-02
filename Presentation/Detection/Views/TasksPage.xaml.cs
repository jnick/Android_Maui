// Подключаем модель представления камеры: журнал распознавания
// накапливает именно она, а страница только показывает его
using MauiBluethooth.Presentation.Camera.ViewModels;

// Объявляем пространство имён для представлений раздела «Распознавание»
namespace MauiBluethooth.Presentation.Detection.Views;

// Страница заданий: журнал того, что распознавание обнаружило за время работы.
//
// Как и страница диагностики, получает готовый CameraViewModel.
// Причина та же: журнал живёт в единственном детекторе, и вторая копия
// данных быстро разошлась бы с настоящей — пользователь увидел бы на этом
// экране не то, что реально нашёл детектор
public partial class TasksPage : ContentPage
{
	// Ссылка на модель представления (внедряется конструктором из DI)
	private readonly CameraViewModel _viewModel;

	// Конструктор страницы: DI внедряет ViewModel
	public TasksPage(CameraViewModel viewModel)
	{
		// Инициализируем элементы интерфейса, объявленные в TasksPage.xaml
		InitializeComponent();

		// Сохраняем ViewModel в поле
		_viewModel = viewModel;

		// Указываем источник данных для всех {Binding} на странице
		BindingContext = _viewModel;
	}
}
