// Подключаем пространство имён, где находится модель представления (ViewModel) этой страницы
using MauiBluethooth.Presentation.Main.ViewModels;

// Объявляем пространство имён для представлений главного раздела
namespace MauiBluethooth.Presentation.Main.Views;

// Объявление частичного класса главной страницы (наследует ContentPage)
public partial class MainPage : ContentPage
{
	// Конструктор страницы. ViewModel передаётся через внедрение зависимостей (DI)
	public MainPage(MainViewModel viewModel)
	{
		// Инициализирует компоненты, объявленные в XAML-файле MainPage.xaml
		InitializeComponent();
		// Привязываем ViewModel как контекст данных страницы
		BindingContext = viewModel;
	}
}