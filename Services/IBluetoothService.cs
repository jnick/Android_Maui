// Подключаем пространство имён, где находятся модели данных приложения (в частности BluetoothDeviceInfo)
using MauiBluethooth.Models;

// Объявляем собственное пространство имён для сервисного слоя приложения
namespace MauiBluethooth.Services;

// Интерфейс — контракт Bluetooth-сервиса, который реализуется отдельно под каждую платформу (Android, Windows)
public interface IBluetoothService
{
	// Признак того, что в данный момент установлено соединение с устройством
	bool IsConnected { get; }

	// Событие: найдено очередное Bluetooth-устройство. Передаёт подписчику информацию об устройстве
	event EventHandler<BluetoothDeviceInfo>? DeviceDiscovered;

	// Событие: процесс поиска устройств завершён (независимо от того, нашлись ли устройства)
	event EventHandler? DiscoveryFinished;

	// Событие: получена строка данных от подключённого устройства
	event EventHandler<string>? DataReceived;

	// Событие: изменилось состояние подключения (передаёт новый статус: true — подключено, false — отключено)
	event EventHandler<bool>? ConnectionChanged;

	// Запрос необходимых разрешений (Bluetooth, геолокация). Возвращает true, если доступ получен
	Task<bool> RequestPermissionsAsync();

	// Запуск поиска Bluetooth-устройств. Возвращает true, если поиск успешно запущен
	Task<bool> StartDiscoveryAsync();

	// Остановка поиска Bluetooth-устройств
	Task StopDiscoveryAsync();

	// Подключение к выбранному устройству. Возвращает true, если подключение установлено
	Task<bool> ConnectAsync(BluetoothDeviceInfo device);

	// Отключение от устройства и освобождение ресурсов соединения
	Task DisconnectAsync();

	// Отправка строки данных устройству (платформа дописывает служебные символы, если требуется)
	Task SendDataAsync(string data);
}