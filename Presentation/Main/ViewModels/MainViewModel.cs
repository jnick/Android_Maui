// Подключаем пространство имён с атрибутом RelayCommand (генерация команд для привязки в XAML)
using CommunityToolkit.Mvvm.Input;

// Объявляем пространство имён для моделей представления главного раздела
namespace MauiBluethooth.Presentation.Main.ViewModels;

// Модель представления главной страницы (частичная для генерации кода Toolkit'ом)
public partial class MainViewModel
{
	// Атрибут генерирует команду GoToDevicesCommand для привязки к кнопке в XAML
	[RelayCommand]
	// Метод команды — переход к экрану устройств
	private async Task GoToDevices()
	{
		// Используем Shell-навигацию: открываем маршрут «devices»
		await Shell.Current.GoToAsync("devices");
	}

	// Атрибут генерирует команду GoToCameraCommand для привязки к кнопке в XAML
	[RelayCommand]

	// Метод команды — переход к экрану камеры
	private async Task GoToCamera()
	{
		// Открываем маршрут «camera», зарегистрированный в AppShell.
		// Благодаря async Shell сам выполнит переход со стеком навигации и анимацией
		await Shell.Current.GoToAsync("camera");
	}

	// Атрибут генерирует команду GoToDiagnosticsCommand для привязки к кнопке в XAML
	[RelayCommand]

	// Метод команды — переход к экрану диагностики распознавания.
	//
	// Экран доступен и с главной страницы, и с экрана камеры. Это не избыточность:
	// с камеры переход делается в один тап во время работы с распознаванием,
	// а с главной — чтобы посмотреть замеры, не включая камеру заново
	private static Task GoToDiagnostics() => Shell.Current.GoToAsync("diagnostics");

	// Атрибут генерирует команду GoToTasksCommand для привязки к кнопке в XAML
	[RelayCommand]

	// Метод команды — переход к журналу найденных объектов
	private static Task GoToTasks() => Shell.Current.GoToAsync("tasks");
}