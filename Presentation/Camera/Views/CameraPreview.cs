// Подключаем интерфейс камерного сервиса
using MauiBluethooth.Services;
// Подключаем пространство имён элементов управления MAUI (View, BindableProperty)
using Microsoft.Maui.Controls;

// Объявляем пространство имён для представлений раздела «Камера»
namespace MauiBluethooth.Presentation.Camera.Views;

// Кроссплатформенный контрол «живого предпросмотра камеры».
// Это «пустая» MAUI-обёртка: сама по себе она ничего не рисует.
// Наполняет её CameraPreviewHandler, который подставляет нативный элемент:
//   Android → AndroidX.Camera.View.PreviewView
//   Windows → Microsoft.UI.Xaml.Controls.MediaPlayerElement
// Такой приём называется «частичный контрол с handler'ом» — стандартный способ добавить
// в MAUI контрол, для которого в фреймворке готовой реализации нет
public sealed class CameraPreview : View
{
	// Объявляем BindableProperty — так в MAUI описываются свойства, доступные из XAML.
	// Почему не auto-property: только BindableProperty поддерживает привязку ({Binding CameraService})
	// и уведомление UI об изменениях
	public static readonly BindableProperty CameraServiceProperty = BindableProperty.Create(

		// 1) Имя свойства в C# — по нему работает привязка
		nameof(CameraService),

		// 2) Тип значения свойства — обязателен, чтобы движок привязки знал, что проверять
		typeof(ICameraService),

		// 3) Владелец свойства — наш контрол. По нему XAML-компилятор поймёт, что свойство существует
		typeof(CameraPreview),

		// 4) Значение по умолчанию: сервиса нет, handler сообщит об ошибке при создании
		null);

	// Камерный сервис, который создаст нативный элемент предпросмотра.
	// В XAML задаётся так: CameraService="{Binding CameraService}"
	// (BindingContext — CameraViewModel, у которого это свойство отдаёт тот же сервис)
	public ICameraService? CameraService
	{
		// Геттер читает значение из хранилища BindableProperty и приводит к нужному типу.
		// Приведение нужно, потому что внутри хранилище всё хранится как object
		get => (ICameraService?)GetValue(CameraServiceProperty);

		// Сеттер записывает значение в хранилище BindableProperty
		set => SetValue(CameraServiceProperty, value);
	}
}
