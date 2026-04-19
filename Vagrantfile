Vagrant.configure("2") do |config|
  # The most trusted Windows 11 image for Vagrant
  config.vm.box = "gusztavvargadr/windows-11"
  
  # Force Vagrant to use WinRM instead of SSH to communicate with the OS
  config.vm.communicator = "winrm"

  # Loop to create 2 Windows VMs
  (1..2).each do |i|
    config.vm.define "win-node-#{i}" do |node|
      
      node.vm.provider "hyperv" do |hv|
        hv.cpus = 2
        hv.memory = 4096 # 4GB is the absolute minimum for Win 11
        hv.vmname = "softserve-win11-0#{i}" 
      end
      
    end
  end
end