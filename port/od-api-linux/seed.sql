-- Synthetic seed for od-api-linux smoke tests. Disposable database only. Never real data.
INSERT INTO apikey (CustApiKey,DevName) VALUES ('POC-CUST-KEY-1','POCDEV');
INSERT INTO preference (PrefName,ValueString) VALUES ('DateLastAging','2026-10-01');
INSERT INTO patient (LName,FName,MiddleI,Preferred,PatStatus,Gender,Position,Birthdate,SSN,Address,City,`State`,Zip,HmPhone,Guarantor,Email,EstBalance,PriProv,SecProv,FeeSched,BillingType,ClinicNum)
VALUES ('Harrison','Bradley','V','Brad',0,0,1,'1986-11-06','','201 S Burnett Ln','Lakewood','Maine','12345','5552523366',1,'',0,1,0,0,0,1);
UPDATE patient SET Guarantor=PatNum WHERE PatNum=1;
INSERT INTO appointment (PatNum,AptStatus,Pattern,Confirmed,TimeLocked,`Op`,ProvNum,ProvHyg,AptDateTime,NextAptNum,UnschedStatus,IsNewPatient,Assistant,ClinicNum,IsHygiene,DateTimeArrived,DateTimeSeated,DateTimeDismissed,InsPlan1,InsPlan2,DateTimeAskedToArrive,ProcDescript)
VALUES (1,1,'//XXXX//',19,0,3,1,0,'2020-07-31 08:30:00',0,0,0,0,0,0,'0001-01-01 00:00:00','0001-01-01 00:00:00','0001-01-01 00:00:00',3,0,'0001-01-01 00:00:00','Seal, Seal');
INSERT INTO apisubscription (EndPointUrl,Workstation,CustomerKey,WatchTable,PollingSeconds,UiEventType,DateTimeStart,Note)
VALUES ('http://localhost:8080/__test/sink','ODLINUX','POC-CUST-KEY-1','Appointment',5,'','2020-01-01 00:00:00','synthetic events smoke test');
