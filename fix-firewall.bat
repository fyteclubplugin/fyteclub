@echo off
echo Creating Windows Firewall rules for FyteClub TURN server...
netsh advfirewall firewall add rule name="FyteClub TURN 49878" dir=in action=allow protocol=UDP localport=49878
netsh advfirewall firewall add rule name="FyteClub TURN 49878 TCP" dir=in action=allow protocol=TCP localport=49878
echo Done! FyteClub TURN server should now work.
pause