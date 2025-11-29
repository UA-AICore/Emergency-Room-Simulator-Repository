#!/usr/bin/expect -f

# Expect script to upload and deploy with password authentication
set timeout 60

set password "COY BID MY PUG AND VAN DEAF FOGY GORY BORN JUDO"
set server "exouser@149.165.154.35"
set zip_file "ERSimulatorApp-deployment.zip"
set deploy_script "deploy-with-keys.sh"

# Upload deployment package
spawn scp -o StrictHostKeyChecking=accept-new $zip_file $server:~/AppDrop/
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
        puts "Upload complete"
    }
}

# Upload deployment script
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
    timeout {
        puts "\n⚠️  Deployment may still be running, check manually"
    }
}

# Exit SSH session
send "exit\r"
expect eof

