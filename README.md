# PDD_Archivos

La documentación de los endpoints está en 
https://localhost:7116/swagger/index.html

Es neceario contar con una GUI de mongoDB para realizar un cambio en el estado del registro de MongoDB para que funcione el endpoint de upload

necesitas correr los contenedores especificados en

https://github.com/manuelogasxxx/PDA_Files


# Consideraciones

1. No tiene los contratos de errores y hace falta envolver en try-catch las conexiones a MinIO, MongoDB y RabbitMQ
2. NO tiene configuraciones en caso de que el servicio completo se cae para poder reemplazarlo con otro 