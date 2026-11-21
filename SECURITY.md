# Security

Please report a security problem privately, not in a public issue: use **Security → Report a vulnerability** on this repository (GitHub's private vulnerability reporting). Say what is affected (the reader, the updater, pairing, the installer), how to reproduce it and what an attacker gains.

The maintainer answers there. A fix goes out as a release, and installed apps update themselves from it; the advisory is published once they can.

Only the newest release is supported. The dashboard and server have their own policy in [oldbear24/odyle-dashboard](https://github.com/oldbear24/odyle-dashboard/security).

The app reads only its own game client's packets, locally. It sends normalized Odyle readings, the ids of its own character's party and force, and the names of the other players in that party and force for the armory's scout (until the account turns that off on the dashboard); its own character's position only with location sharing on, and its PvP kills, assists and deaths only with PvP stats on. Anything that makes it read or send more is in scope.
