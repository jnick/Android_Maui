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
}