#!/usr/bin/expect -f

# Expect script to re-upload fixed deployment script and deploy
set timeout 300

set password "COY BID MY PUG AND VAN DEAF FOGY GORY BORN JUDO"
set server "exouser@149.165.154.35"
set deploy_script "deploy-with-keys.sh"

# Upload fixed deployment script
spawn scp -o StrictHostKeyChecking=accept-new $deploy_script $server:~/AppDrop/
expect {
    "password:" {
        send "$password\r"
        exp_continue
    }
    "Permission denied" {
        puts "ERROR: Authentication failed"
        exit 1
    }
    eof {
        puts "Script upload complete"
    }
}

# SSH and run deployment
spawn ssh -o StrictHostKeyChecking=accept-new $server
expect {
    "password:" {
        send "$password\r"
    }
}

expect "$ "
send "cd ~/AppDrop\r"
expect "$ "
send "chmod +x deploy-with-keys.sh\r"
expect "$ "
send "./deploy-with-keys.sh\r"

# Wait for deployment to complete
expect {
    "Deployment complete" {
        puts "\n✅ Deployment successful!"
    }
    "Container started successfully" {
        puts "\n✅ Container started!"
    }
    timeout {
        puts "\n⚠️  Deployment may still be running, check manually"
    }
}

# Show final status
expect "$ "
send "docker ps | grep ersimulator\r"
expect "$ "
send "docker logs ersimulator --tail 10\r"
expect "$ "
send "exit\r"
expect eof

