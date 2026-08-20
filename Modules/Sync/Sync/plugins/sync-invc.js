
var sync_invc = {
  // qdl_jut_autopilot — тумблер «автопилота» jut.su (пропуск опенинга + следующая серия).
  // Ключ синкается между устройствами: владелец включает его на телефоне и ждёт того же
  // поведения на ТВ. Импорт идёт на старте и по реконнекту, экспорт — на каждое изменение.
  import_keys: ['qdl_jut_autopilot'] // 'myfavorite', 'profiles'
};


sync_invc.goExport = function goExport(path, value) {
  // можно добавить свои поля или изменить стандартные в синхронизации
  return value;
};

sync_invc.importСompleted  = function importСompleted(path) {
  // импорт завершён, при необходимости можно выполнить дополнительный код
};


// Вызвать export c path 'myfavorite'
// window.lwsEvent.send('sync', 'myfavorite');

// Вызвать export для закладок и просмотров
//window.lwsEvent.send('sync', 'sync_favorite');
//window.lwsEvent.send('sync', 'sync_view');

// Отправить событие по socket_id
// window.lwsEvent.sendId(connectionId, 'openlink', 'json');
